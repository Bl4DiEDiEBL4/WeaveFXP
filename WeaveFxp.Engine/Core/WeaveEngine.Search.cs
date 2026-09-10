using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

public sealed partial class WeaveEngine
{
    private sealed class SearchRun(SiteSearchRequest request)
    {
        public readonly string Id = Guid.NewGuid().ToString("N");
        public readonly SiteSearchRequest Request = request;
        public readonly DateTime StartedAt = DateTime.UtcNow;
        public readonly CancellationTokenSource Stop = new(TimeSpan.FromMinutes(15));
        public readonly object Sync = new();
        public readonly List<SiteSearchResult> Results = new();
        public readonly List<string> Errors = new();
        public string Status = "running";
        public string CurrentPath = "";
        public int Scanned;
        public int Pending;
        public int CommandsCompleted;
        public int CommandsPending;
    }

    private readonly object _searchLock = new();
    private readonly Dictionary<string, SearchRun> _searches = new();

    public SiteSearchSnapshot StartSiteSearch(SiteSearchRequest request)
    {
        request = request with
        {
            Sites = (request.Sites ?? new()).Select(site => site.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Paths = (request.Paths ?? new()).Select(SiteSearchFilter.NormalizePath).Distinct(StringComparer.Ordinal).ToList(),
            Include = request.Include ?? "", Exclude = request.Exclude ?? "", Kind = (request.Kind ?? "both").ToLowerInvariant()
        };
        if (request.Sites.Count is < 1 or > 20 || request.Paths.Count is < 1 or > 20)
            throw new ArgumentException("Select 1-20 sites and 1-20 paths");
        foreach (var name in request.Sites)
            if (_store.Site(name) is null) throw new ArgumentException($"Unknown site: {name}");
        var filter = new SiteSearchFilter(request);
        if (!request.Recursive) NativeSiteSearch.Queries(request);
        var run = new SearchRun(request);
        lock (_searchLock)
        {
            if (_searches.Values.Count(search => { lock (search.Sync) return search.Status == "running"; }) >= 4)
            {
                run.Stop.Dispose();
                throw new InvalidOperationException("Four searches are already running");
            }
            foreach (var old in _searches.Values.OrderBy(search => search.StartedAt).ToList())
            {
                if (_searches.Count < 20) break;
                lock (old.Sync)
                    if (old.Status != "running") { _searches.Remove(old.Id); old.Stop.Dispose(); }
            }
            _searches.Add(run.Id, run);
        }
        _ = Task.Run(() => RunSiteSearchAsync(run, filter));
        return SearchSnapshot(run, 0, 100);
    }

    public SiteSearchSnapshot? SiteSearch(string id, int offset = 0, int limit = 100)
    {
        lock (_searchLock)
            return _searches.TryGetValue(id, out var run) ? SearchSnapshot(run, Math.Max(0, offset), Math.Clamp(limit, 1, 10000)) : null;
    }

    private static SiteSearchSnapshot SearchSnapshot(SearchRun run, int offset, int limit)
    {
        lock (run.Sync)
            return new(run.Id, run.Status, run.Request with { Sites = new(run.Request.Sites), Paths = new(run.Request.Paths) },
                run.StartedAt, run.Scanned, run.Pending, run.Results.Count, run.CurrentPath,
                run.Results.Skip(offset).Take(limit).ToList(), run.Errors.ToList(), run.CommandsCompleted, run.CommandsPending);
    }

    public bool StopSiteSearch(string id)
    {
        lock (_searchLock)
        {
            if (!_searches.TryGetValue(id, out var run)) return false;
            run.Stop.Cancel();
            return true;
        }
    }

    private async Task RunSiteSearchAsync(SearchRun run, SiteSearchFilter filter)
    {
        if (!run.Request.Recursive)
        {
            await RunNativeSiteSearchAsync(run, filter).ConfigureAwait(false);
            return;
        }
        var todo = new Queue<(string Site, string Path, int Depth)>();
        var seen = new HashSet<(string, string)>();
        foreach (var site in run.Request.Sites)
            foreach (var path in run.Request.Paths)
                if (seen.Add((site, path))) todo.Enqueue((site, path, 0));
        var emitted = new HashSet<(string, string)>();
        var ct = run.Stop.Token;
        try
        {
            while (todo.TryDequeue(out var folder))
            {
                ct.ThrowIfCancellationRequested();
                lock (run.Sync) { run.CurrentPath = folder.Site + ": " + folder.Path; run.Pending = todo.Count + 1; }
                List<RemoteEntry> entries;
                try { entries = await ListForSearchAsync(folder.Site, folder.Path, ct).ConfigureAwait(false); }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    lock (run.Sync)
                    {
                        run.Scanned++;
                        if (run.Errors.Count < 100) run.Errors.Add(folder.Site + ": " + folder.Path + ": " + ex.Message);
                    }
                    continue;
                }
                lock (run.Sync) run.Scanned++;
                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!SiteSearchFilter.SafeName(entry.Name) || filter.Excluded(entry.Name)) continue;
                    var path = FtpClient.JoinRemote(folder.Path, entry.Name);
                    if (filter.Matches(entry) && emitted.Add((folder.Site, path)))
                    {
                        lock (run.Sync)
                        {
                            run.Results.Add(new(run.Results.Count, folder.Site, entry.Name, path, entry.Type, entry.Size, entry.Modified, entry.Owner, entry.Group));
                            if (run.Results.Count >= run.Request.MaxResults) { run.Status = "limited"; return; }
                        }
                    }
                    // Do not follow symlinks or let name filters prune unmatched ancestors.
                    if (entry.Type == "dir" && folder.Depth < run.Request.MaxDepth && seen.Add((folder.Site, path)))
                    {
                        if (seen.Count > 20000) { lock (run.Sync) run.Status = "limited"; return; }
                        todo.Enqueue((folder.Site, path, folder.Depth + 1));
                    }
                }
            }
            lock (run.Sync) run.Status = run.Errors.Count == 0 ? "completed" : "partial";
        }
        catch (Exception ex)
        {
            lock (run.Sync)
            {
                run.Status = ct.IsCancellationRequested ? "cancelled" : "failed";
                if (!ct.IsCancellationRequested) run.Errors.Add(ex.Message);
            }
        }
        finally
        {
            lock (run.Sync) { run.Pending = 0; run.CurrentPath = ""; }
            NotifyChanged();
        }
    }

    private async Task RunNativeSiteSearchAsync(SearchRun run, SiteSearchFilter filter)
    {
        var queries = NativeSiteSearch.Queries(run.Request);
        var emitted = new HashSet<(string, string)>();
        var ct = run.Stop.Token;
        lock (run.Sync) run.CommandsPending = queries.Length * run.Request.Sites.Count;
        try
        {
            foreach (var name in run.Request.Sites)
            {
                foreach (var query in queries)
                {
                    ct.ThrowIfCancellationRequested();
                    lock (run.Sync) run.CurrentPath = name + ": SITE SEARCH " + query;
                    List<RemoteEntry> entries;
                    try { entries = await NativeSearchOnSiteAsync(name, query, ct).ConfigureAwait(false); }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        lock (run.Sync) run.Errors.Add(name + ": " + ex.Message);
                        // No retry or recursive fallback when SITE SEARCH is unavailable.
                        break;
                    }
                    finally { lock (run.Sync) { run.CommandsCompleted++; run.CommandsPending--; } }
                    foreach (var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!run.Request.Paths.Any(root => root == "/" || entry.Path.Equals(root, StringComparison.Ordinal) || entry.Path.StartsWith(root + "/", StringComparison.Ordinal))) continue;
                        if (entry.Path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(filter.Excluded) || !emitted.Add((name, entry.Path))) continue;
                        lock (run.Sync)
                        {
                            run.Results.Add(new(run.Results.Count, name, entry.Name, entry.Path, entry.Type, 0, default, "", ""));
                            if (run.Results.Count >= run.Request.MaxResults) { run.Status = "limited"; return; }
                        }
                    }
                }
            }
            lock (run.Sync) run.Status = run.Errors.Count == 0 ? "completed" : "partial";
        }
        catch (Exception ex)
        {
            lock (run.Sync)
            {
                run.Status = ct.IsCancellationRequested ? "cancelled" : "failed";
                if (!ct.IsCancellationRequested) run.Errors.Add(ex.Message);
            }
        }
        finally
        {
            lock (run.Sync) { run.CommandsPending = 0; run.CurrentPath = ""; }
            NotifyChanged();
        }
    }

    private async Task<List<RemoteEntry>> NativeSearchOnSiteAsync(string name, string query, CancellationToken ct)
    {
        var site = _store.Site(name) ?? throw new IOException($"Unknown site: {name}");
        var pool = AcquirePool(name, site, FtpConfig(site, "", false));
        FtpClient? client = null;
        try
        {
            client = await pool.BorrowAsync(ct).ConfigureAwait(false);
            using var cancel = ct.Register(client.Dispose);
            var (code, message) = await client.CommandAsync("SITE SEARCH " + query).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (code / 100 != 2) throw new IOException($"SITE SEARCH failed: {code} {message}");
            return NativeSiteSearch.Parse(message);
        }
        catch
        {
            if (client is not null) { pool.Drop(client); client = null; }
            throw;
        }
        finally
        {
            if (client is not null) pool.Return(client);
            ReleasePool(name);
        }
    }

    private async Task<List<RemoteEntry>> ListForSearchAsync(string name, string path, CancellationToken ct)
    {
        var site = _store.Site(name) ?? throw new IOException($"Unknown site: {name}");
        var pool = AcquirePool(name, site, FtpConfig(site, "", false));
        FtpClient? client = null;
        try
        {
            client = await pool.BorrowAsync(ct).ConfigureAwait(false);
            using var cancel = ct.Register(client.Dispose);
            var entries = await client.ListAsync(path, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return entries;
        }
        catch
        {
            if (client is not null) { pool.Drop(client); client = null; }
            throw;
        }
        finally
        {
            if (client is not null) pool.Return(client);
            ReleasePool(name);
        }
    }

    public List<Job> QueueSiteSearchResults(string id, SiteSearchQueueRequest request)
    {
        request = request with { DestinationSite = (request.DestinationSite ?? "local").Trim(), DestinationPath = (request.DestinationPath ?? "").Trim() };
        var snapshot = SiteSearch(id, 0, 10000) ?? throw new ArgumentException("Search not found");
        var ids = (request.ResultIds ?? new()).Distinct().ToHashSet();
        if (ids.Count is < 1 or > 500) throw new ArgumentException("Select 1-500 results");
        var selected = snapshot.Results.Where(result => ids.Contains(result.Id)).ToList();
        if (selected.Count != ids.Count) throw new ArgumentException("Unknown search result ID");
        selected = selected.Where(result => !selected.Any(parent => parent.Id != result.Id && parent.Site == result.Site &&
            parent.Kind == "dir" && result.Path.StartsWith(parent.Path + "/", StringComparison.Ordinal))).ToList();
        var local = request.DestinationSite.Equals("local", StringComparison.OrdinalIgnoreCase);
        if (!local && _store.Site(request.DestinationSite) is null) throw new ArgumentException("Unknown destination site");
        var root = local ? (string.IsNullOrWhiteSpace(request.DestinationPath) ? DownloadBase() : Path.GetFullPath(request.DestinationPath))
            : SiteSearchFilter.NormalizePath(request.DestinationPath);
        if (selected.Any(result => !local && result.Site.Equals(request.DestinationSite, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Source and destination site must differ");
        if (local && selected.Any(result => result.Name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || result.Name.EndsWith('.') || result.Name.EndsWith(' ')))
            throw new ArgumentException("A selected result has a name that cannot be downloaded locally");
        if (selected.Select(result => result.Name).Distinct(local ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).Count() != selected.Count)
            throw new ArgumentException("Selected results share a destination name; queue them into separate folders");
        return selected.Select(result => local
            ? StartDownload(new DownloadRequest { Site = result.Site, SourcePath = result.Path, DestPath = Path.Combine(root, result.Name) })
            : StartFxp(new TransferRequest { FromSite = result.Site, ToSite = request.DestinationSite, SourcePath = result.Path, DestPath = FtpClient.JoinRemote(root, result.Name) })).ToList();
    }
}
