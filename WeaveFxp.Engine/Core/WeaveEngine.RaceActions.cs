using System.Collections.Concurrent;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

// cbftp spread-job actions for races:
//   while running : remove a site (+ own files / + all files), abort (+ delete own files
//                   on incomplete sites / on ALL sites), add a site, remove a site from
//                   every running race
//   after the end : delete own files / all files on a site, own files on ALL sites
//   any time      : reset (restart with all sites) / hard reset (also forget file history)
// "Own files" are the files WeaveFXP itself uploaded to that site in this race (the job's
// per-file rows with status "done"), so they can be cleaned up after the race too.
public sealed partial class WeaveEngine
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _raceRemovedSites =
        new(StringComparer.OrdinalIgnoreCase);
    private long _raceRouteVersion; // bumped on every removal so a running mesh recomputes routes
    private readonly HashSet<string> _raceMutations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (string Job, string Site)> _racePrecreates = new();

    private bool HasRacePrecreate(string id, string? site = null) => _racePrecreates.Values.Any(op =>
        op.Job.Equals(id, StringComparison.OrdinalIgnoreCase) && (site is null || op.Site.Equals(site, StringComparison.OrdinalIgnoreCase)));

    private async Task<RaceActionResult> RunRaceMutationAsync(string id, Func<Task<RaceActionResult>> action)
    {
        lock (_raceAnnounceLock)
            if (!_raceMutations.Add(id)) return Fail(new(), "another race action is still running");
        try { return await action().ConfigureAwait(false); }
        finally { lock (_raceAnnounceLock) _raceMutations.Remove(id); }
    }

    private bool IsRemovedFromRace(string jobId, string site) =>
        _raceRemovedSites.TryGetValue(jobId, out var sites) && sites.ContainsKey(site);

    // ---- read side (UI) ----------------------------------------------------------------

    public List<string> RaceSiteNames(string jobId)
    {
        var job = _store.JobHeader(jobId);
        return job is null ? new() : RaceSitesOf(job).Where(s => !IsRemovedFromRace(jobId, s)).ToList();
    }

    public List<string> RaceRemovedSiteNames(string jobId) =>
        _raceRemovedSites.TryGetValue(jobId, out var sites) ? sites.Keys.OrderBy(s => s).ToList() : new();

    // Own (uploaded by us) file count per site; includes sites already removed.
    public Dictionary<string, int> RaceOwnFileCounts(string jobId)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var job = _store.Job(jobId);
        if (job is null) return result;
        foreach (var site in RaceSitesOf(job)) result[site] = 0;
        foreach (var group in job.Files.Where(f => f.Status == "done" && !string.IsNullOrWhiteSpace(f.ToSite))
                     .GroupBy(f => f.ToSite.Trim(), StringComparer.OrdinalIgnoreCase))
            result[group.Key] = group.Select(f => f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return result;
    }

    public string? RaceReleasePath(string jobId, string site) =>
        _store.JobHeader(jobId) is { } job ? ReleasePathOf(job, site) : null;

    // Release path on a site that is not in the race yet: find the section (by its NAME)
    // the release lives in on a race site, then the same-named section on the new site.
    public string? SuggestRacePathForSite(string jobId, string site)
    {
        var job = _store.JobHeader(jobId);
        var target = _store.Site((site ?? "").Trim());
        if (job is null || target is null) return null;
        foreach (var name in RaceSitesOf(job))
        {
            var raceSite = _store.Site(name);
            var path = ReleasePathOf(job, name);
            if (raceSite is null || string.IsNullOrWhiteSpace(path)) continue;
            var parent = TrimDir(RemoteParentPath(path));
            var section = raceSite.Sections.FirstOrDefault(s => TrimDir(s.Section).Equals(parent, StringComparison.OrdinalIgnoreCase));
            if (section is null) continue;
            var mapped = target.Sections.FirstOrDefault(s => s.Name.Equals(section.Name, StringComparison.OrdinalIgnoreCase));
            if (mapped is null || string.IsNullOrWhiteSpace(mapped.Section)) continue;
            return FtpClient.JoinRemote(TrimDir(mapped.Section), RemoteBase(path));
        }
        return null;
    }

    // ---- actions -----------------------------------------------------------------------

    // d / D while running: take the site out of the race (it gets no more files and serves
    // none), then optionally delete our own files (d) or the whole release dir (D) there.
    public Task<RaceActionResult> RemoveSiteFromRaceAsync(string jobId, string site, RaceFileScope delete,
        CancellationToken ct = default) =>
        RunRaceMutationAsync(jobId, () => RemoveSiteFromRaceCoreAsync(jobId, site, delete, ct));

    private async Task<RaceActionResult> RemoveSiteFromRaceCoreAsync(string jobId, string site, RaceFileScope delete,
        CancellationToken ct = default)
    {
        var result = new RaceActionResult();
        site = (site ?? "").Trim();
        var job = _store.JobHeader(jobId);
        if (job is null || job.Type != JobType.Race) return Fail(result, "race not found");
        if (!RaceSitesOf(job).Contains(site, StringComparer.OrdinalIgnoreCase)) return Fail(result, $"{site} is not in this race");
        if (!job.Terminal)
        {
            RemoveSiteCore(jobId, site, result);
            if (delete != RaceFileScope.None)
                if (!await WaitForSiteTransfersAsync(jobId, site, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
                    return Fail(result, "site transfers are still active; no files deleted");
        }
        if (delete != RaceFileScope.None)
            Merge(result, await DeleteOnSiteAsync(jobId, site, delete, onlyIncomplete: false, ct).ConfigureAwait(false));
        return result;
    }

    // X: remove one site from every running race (no files deleted).
    public RaceActionResult RemoveSiteFromAllRaces(string site)
    {
        var result = new RaceActionResult();
        site = (site ?? "").Trim();
        foreach (var job in _store.Jobs().Where(j => j.Type == JobType.Race && !j.Terminal).ToList())
        {
            if (!RaceSitesOf(job).Contains(site, StringComparer.OrdinalIgnoreCase) || IsRemovedFromRace(job.Id, site)) continue;
            var part = new RaceActionResult();
            RemoveSiteCore(job.Id, site, part);
            result.Lines.Add($"{RaceLabel(job)}: {string.Join("; ", part.Lines)}");
        }
        if (result.Lines.Count == 0) result.Lines.Add($"{site} is not in any running race");
        return result;
    }

    // B / z / Z: abort the race, optionally deleting our own files on the sites that are
    // not complete (z) or on every site (Z).
    public Task<RaceActionResult> AbortRaceAsync(string jobId, RaceFileScope delete, bool onlyIncomplete,
        CancellationToken ct = default) =>
        RunRaceMutationAsync(jobId, () => AbortRaceCoreAsync(jobId, delete, onlyIncomplete, ct));

    private async Task<RaceActionResult> AbortRaceCoreAsync(string jobId, RaceFileScope delete, bool onlyIncomplete,
        CancellationToken ct = default)
    {
        var result = new RaceActionResult();
        var job = _store.JobHeader(jobId);
        if (job is null || job.Type != JobType.Race) return Fail(result, "race not found");
        if (!job.Terminal)
        {
            CancelJobInternal(jobId, "Aborted by user");
            result.Lines.Add("race aborted");
            if (!await WaitForNoActiveTransfersAsync(jobId, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
                return Fail(result, "race is still stopping; no files deleted");
        }
        if (delete != RaceFileScope.None)
            Merge(result, await DeleteRaceFilesCoreAsync(jobId, null, delete, onlyIncomplete, ct).ConfigureAwait(false));
        return result;
    }

    // d / D / Z after the race: delete own or all files on one site (site set) or own files
    // on every site we uploaded to (site null). Sites are processed in parallel.
    public Task<RaceActionResult> DeleteRaceFilesAsync(string jobId, string? site, RaceFileScope scope,
        bool onlyIncomplete, CancellationToken ct = default) =>
        RunRaceMutationAsync(jobId, () => DeleteRaceFilesCoreAsync(jobId, site, scope, onlyIncomplete, ct));

    private async Task<RaceActionResult> DeleteRaceFilesCoreAsync(string jobId, string? site, RaceFileScope scope,
        bool onlyIncomplete, CancellationToken ct = default)
    {
        var result = new RaceActionResult();
        if (scope == RaceFileScope.None) return result;
        var job = _store.Job(jobId);
        if (job is null || job.Type != JobType.Race) return Fail(result, "race not found");
        List<string> targets;
        if (!string.IsNullOrWhiteSpace(site)) targets = new() { site.Trim() };
        else if (scope == RaceFileScope.Own)
            targets = job.Files.Where(f => f.Status == "done" && !string.IsNullOrWhiteSpace(f.ToSite))
                .Select(f => f.ToSite.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        else
            return Fail(result, "deleting ALL files needs one site");
        if (targets.Count == 0) { result.Lines.Add("no own files on any site"); return result; }
        var parts = await Task.WhenAll(targets.Select(t => DeleteOnSiteAsync(jobId, t, scope, onlyIncomplete, ct))).ConfigureAwait(false);
        foreach (var part in parts) Merge(result, part);
        return result;
    }

    // r / R: restart the race with all its sites. Hard reset also forgets the per-file
    // history (so own-file tracking starts over too).
    public RaceActionResult ResetRace(string jobId, bool hard)
    {
        lock (_raceAnnounceLock)
        {
            if (_raceMutations.Contains(jobId) || _jobRuns.ContainsKey(jobId) || _store.JobHeader(jobId) is { Terminal: false })
                return Fail(new(), "race is still active; use the asynchronous reset");
            return ResetRaceCore(jobId, hard);
        }
    }

    public Task<RaceActionResult> ResetRaceAsync(string jobId, bool hard, CancellationToken ct = default) =>
        RunRaceMutationAsync(jobId, async () =>
        {
            if (_store.JobHeader(jobId) is not { Type: JobType.Race } job) return Fail(new(), "race not found");
            if (!job.Terminal) CancelJobInternal(jobId, hard ? "Hard reset" : "Reset");
            if (!await WaitForNoActiveTransfersAsync(jobId, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
                return Fail(new(), "race is still stopping; reset was not started");
            lock (_raceAnnounceLock) return ResetRaceCore(jobId, hard);
        });

    private RaceActionResult ResetRaceCore(string jobId, bool hard)
    {
        var result = new RaceActionResult();
        var job = _store.JobHeader(jobId);
        if (job is null || job.Type != JobType.Race) return Fail(result, "race not found");
        _raceRemovedSites.TryRemove(jobId, out _);
        Interlocked.Increment(ref _raceRouteVersion);
        if (hard)
        {
            if (!job.Terminal) CancelJobInternal(jobId, "Hard reset");
            _store.UpdateJob(jobId, j =>
            {
                j.Files.Clear();
                j.SfvFiles.Clear();
                j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "warn", Message = "— hard reset: file history cleared —" });
            });
        }
        if (!RetryJobCore(jobId)) return Fail(result, "could not restart the race");
        result.Lines.Add(hard ? "hard reset: race restarted from scratch" : "reset: race restarted with all its sites");
        NotifyChanged();
        return result;
    }

    // A: add a site. The running race is continued as a new race that includes the site
    // (started first, so a refusal leaves the current race untouched).
    public RaceActionResult AddSiteToRace(string jobId, string site, string? path)
    {
        lock (_raceAnnounceLock)
        {
            if (_raceMutations.Contains(jobId)) return Fail(new(), "another race action is still running");
            return AddSiteToRaceCore(jobId, site, path);
        }
    }

    private RaceActionResult AddSiteToRaceCore(string jobId, string site, string? path)
    {
        var result = new RaceActionResult();
        site = (site ?? "").Trim();
        var job = _store.JobHeader(jobId);
        if (job is null || job.Type != JobType.Race) return Fail(result, "race not found");
        if (_store.Site(site) is null) return Fail(result, $"site {site} not found");
        var sites = RaceSitesOf(job).Where(s => !IsRemovedFromRace(jobId, s)).ToList();
        if (sites.Contains(site, StringComparer.OrdinalIgnoreCase)) return Fail(result, $"{site} is already in this race");
        path = string.IsNullOrWhiteSpace(path) ? SuggestRacePathForSite(jobId, site) : path.Trim();
        if (string.IsNullOrWhiteSpace(path))
            return Fail(result, $"no matching section on {site}: enter the release path for {site}");
        if (!job.Terminal && IsRemovedFromRace(jobId, site) &&
            string.Equals(path, ReleasePathOf(job, site), StringComparison.Ordinal))
        {
            _raceRemovedSites[jobId].TryRemove(site, out _);
            Interlocked.Increment(ref _raceRouteVersion);
            result.NewJobId = jobId;
            result.Lines.Add($"{site} restored to the race");
            NotifyChanged();
            return result;
        }

        var sitePaths = new Dictionary<string, string>(job.Request.SitePaths, StringComparer.OrdinalIgnoreCase);
        foreach (var s in sites)
            if (!sitePaths.ContainsKey(s) && ReleasePathOf(job, s) is { } p) sitePaths[s] = p;
        sitePaths[site] = path;
        var from = sites.FirstOrDefault(s => s.Equals(job.Request.FromSite, StringComparison.OrdinalIgnoreCase)) ?? sites.FirstOrDefault() ?? site;
        var spread = new SpreadRequest
        {
            FromSite = from,
            ToSites = sites.Where(s => !s.Equals(from, StringComparison.OrdinalIgnoreCase)).Append(site).ToList(),
            SourcePath = sitePaths.TryGetValue(from, out var sourcePath) ? sourcePath : job.Request.SourcePath,
            DestPath = path,
            SitePaths = sitePaths,
            DownloadOnlySites = (job.Request.DownloadOnlySites ?? new()).ToList(),
            PreFirstTargets = (job.Request.PreFirstTargets ?? new()).ToList(),
            Label = job.Request.Label,
            Race = true,
            DryRun = job.Request.DryRun,
            ViaApi = job.Request.ViaApi,
        };
        SpreadResult started;
        try { started = StartSpread(spread, queueOnly: job.Paused); }
        catch (Exception ex) { return Fail(result, $"could not add {site}: {FirstLineOf(ex.Message)}"); }
        result.NewJobId = started.Jobs.FirstOrDefault()?.Id;
        if (!job.Terminal && !string.Equals(result.NewJobId, jobId, StringComparison.OrdinalIgnoreCase))
            CancelJobInternal(jobId, $"continued as {result.NewJobId} with {site} added");
        LogJob(jobId, "info", $"{site} added ({path}); the race continues as {result.NewJobId}");
        result.Lines.Add($"{site} added ({path}); race continues as {result.NewJobId}");
        NotifyChanged();
        return result;
    }

    // ---- internals ---------------------------------------------------------------------

    private void RemoveSiteCore(string jobId, string site, RaceActionResult result)
    {
        _raceRemovedSites.GetOrAdd(jobId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[site] = 0;
        Interlocked.Increment(ref _raceRouteVersion);
        var job = _store.JobHeader(jobId);
        var left = job is null ? 0 : RaceSitesOf(job).Count(s => !IsRemovedFromRace(jobId, s));
        LogJob(jobId, "warn", $"{site} removed from the race");
        Log("transfer", site, "warn", $"{(job is null ? jobId : RaceLabel(job))}: {site} removed from the race");
        result.Lines.Add($"{site} removed from the race");
        if (left < 2 && job is { Terminal: false })
        {
            CancelJobInternal(jobId, $"{site} removed; fewer than 2 sites left");
            result.Lines.Add("race stopped: fewer than 2 sites left");
        }
        NotifyChanged();
    }

    private async Task<RaceActionResult> DeleteOnSiteAsync(string jobId, string site, RaceFileScope scope,
        bool onlyIncomplete, CancellationToken ct)
    {
        var result = new RaceActionResult();
        var job = _store.Job(jobId);
        var siteCfg = _store.Site(site);
        if (job is null || siteCfg is null) return Fail(result, $"{site}: race or site not found");
        if (!RaceSitesOf(job).Contains(site, StringComparer.OrdinalIgnoreCase)) return Fail(result, $"{site}: not in this race");
        if (scope is not (RaceFileScope.Own or RaceFileScope.All)) return Fail(result, "invalid deletion scope");
        if (!job.Terminal && !IsRemovedFromRace(jobId, site)) return Fail(result, $"{site}: remove or stop the active race before deleting files");
        if (job.Terminal && !await WaitForNoActiveTransfersAsync(jobId, TimeSpan.FromSeconds(20), ct).ConfigureAwait(false))
            return Fail(result, "race is still stopping; no files deleted");
        if (!job.Terminal && !await WaitForSiteTransfersAsync(jobId, site, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false))
            return Fail(result, "site transfers are still active; no files deleted");
        var root = ReleasePathOf(job, site);
        if (!SafeRacePath(root, relative: false, out root)) return Fail(result, $"{site}: unsafe release path");
        var own = OwnFilesOf(job, site);
        if (own.Any(file => !SafeRacePath(file, relative: true, out _))) return Fail(result, $"{site}: unsafe own-file history path");
        if (scope == RaceFileScope.Own && own.Count == 0)
        {
            result.Lines.Add($"{site}: no own files to delete");
            return result;
        }
        var pool = AcquirePool(site, siteCfg, FtpConfig(siteCfg, site, false));
        FtpClient? client = null;
        try
        {
            client = await pool.BorrowAsync(ct).ConfigureAwait(false);
            if (onlyIncomplete)
            {
                var check = await CheckReleaseOnAsync(client, siteCfg, root, persist: false, ct).ConfigureAwait(false);
                if (check.State == ReleaseState.Complete)
                {
                    result.Lines.Add($"{site}: complete, left alone");
                    return result;
                }
            }
            if (scope == RaceFileScope.All)
            {
                await DeleteRemoteTreeAsync(client, root, ct).ConfigureAwait(false);
                result.Lines.Add($"{site}: deleted the release dir {root}");
                LogJob(jobId, "warn", $"{site}: deleted ALL files ({root})");
                Log("system", site, "warn", $"race action: deleted {root}");
                return result;
            }

            var deleted = 0;
            foreach (var group in own.GroupBy(RelParent, StringComparer.OrdinalIgnoreCase))
            {
                var dir = group.Key.Length == 0 ? root : FtpClient.JoinRemote(root, group.Key);
                var (cwd, cwdMsg) = await client.CommandAsync("CWD " + dir).ConfigureAwait(false);
                if (cwd / 100 != 2)
                {
                    result.Ok = false;
                    result.Lines.Add($"{site}: CWD {dir}: {cwd} {FirstLineOf(cwdMsg)}");
                    continue;
                }
                foreach (var rel in group)
                {
                    var (code, msg) = await client.CommandAsync("DELE " + RemoteBase(rel)).ConfigureAwait(false);
                    if (code / 100 == 2) { deleted++; continue; }
                    if (msg.Contains("no such", StringComparison.OrdinalIgnoreCase) ||
                        msg.Contains("not found", StringComparison.OrdinalIgnoreCase)) continue; // already gone
                    result.Ok = false;
                    result.Lines.Add($"{site}: DELE {rel}: {code} {FirstLineOf(msg)}");
                }
            }
            // Tidy dirs we uploaded into once they are empty (deepest first), then the
            // release dir itself. RMD simply fails while anything else is still in there.
            foreach (var sub in own.Select(RelParent).Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(d => d.Count(c => c == '/')))
            {
                var full = FtpClient.JoinRemote(root, sub);
                await client.EnsureCwdAsync(RemoteParentPath(full)).ConfigureAwait(false);
                await client.CommandAsync("RMD " + RemoteBase(full)).ConfigureAwait(false);
            }
            await client.EnsureCwdAsync(RemoteParentPath(root)).ConfigureAwait(false);
            var (rmd, _) = await client.CommandAsync("RMD " + RemoteBase(root)).ConfigureAwait(false);
            result.Lines.Add($"{site}: deleted {deleted} of {own.Count} own file(s)" +
                (rmd / 100 == 2 ? "; the release dir was empty and is removed" : ""));
            LogJob(jobId, "warn", $"{site}: deleted {deleted} own file(s)");
            Log("system", site, "warn", $"race action: deleted {deleted} own file(s) in {root}");
        }
        catch (OperationCanceledException)
        {
            result.Ok = false;
            throw;
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Lines.Add($"{site}: {FirstLineOf(ex.Message)}");
        }
        finally
        {
            // Failed or cancelled command streams cannot be returned to a shared pool.
            if (client is not null)
            {
                if (result.Ok && !ct.IsCancellationRequested) pool.Return(client);
                else pool.Drop(client);
            }
            ReleasePool(site);
        }
        return result;
    }

    private async Task<bool> WaitForSiteTransfersAsync(string jobId, string site, TimeSpan max, CancellationToken ct)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            var job = _store.Job(jobId);
            if (!HasRacePrecreate(jobId, site) && (job is null || !job.Files.Any(f => f.Status == "active" &&
                    (f.ToSite.Equals(site, StringComparison.OrdinalIgnoreCase) || f.FromSite.Equals(site, StringComparison.OrdinalIgnoreCase)))))
                return true;
            if (job is { Terminal: true } && !_jobRuns.ContainsKey(jobId) && !HasRacePrecreate(jobId, site)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<bool> WaitForNoActiveTransfersAsync(string jobId, TimeSpan max, CancellationToken ct)
    {
        var until = DateTime.UtcNow + max;
        while (DateTime.UtcNow < until)
        {
            if (!_jobRuns.ContainsKey(jobId) && !HasRacePrecreate(jobId)) return true;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    private static List<string> RaceSitesOf(Job job)
    {
        var req = job.Request;
        var names = req.MeshSites.Count > 1 ? req.MeshSites : new List<string> { req.FromSite, req.ToSite };
        return names.Select(n => (n ?? "").Trim())
            .Where(n => n.Length > 0 && !n.Equals("mesh", StringComparison.OrdinalIgnoreCase) && !n.Equals("local", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? ReleasePathOf(Job job, string site)
    {
        var req = job.Request;
        if (req.SitePaths.TryGetValue(site, out var path) && !string.IsNullOrWhiteSpace(path)) return path.Trim();
        if (site.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase)) return req.SourcePath;
        if (site.Equals(req.ToSite, StringComparison.OrdinalIgnoreCase)) return req.DestPath;
        return null;
    }

    private static List<string> OwnFilesOf(Job job, string site) => job.Files
        .Where(f => f.Status == "done" && f.ToSite.Equals(site, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.Name))
        .Select(f => f.Name)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private static bool SafeRacePath(string? value, bool relative, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl)) return false;
        var path = value.Trim().Replace('\\', '/');
        if (relative && path.StartsWith('/')) return false;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p is "." or "..")) return false;
        normalized = (relative ? "" : "/") + string.Join('/', parts);
        return true;
    }

    private static string RelParent(string rel)
    {
        var slash = rel.LastIndexOf('/');
        return slash <= 0 ? "" : rel[..slash];
    }

    private static string TrimDir(string? path)
    {
        var p = "/" + (path ?? "").Trim().Replace('\\', '/').Trim('/');
        return p.Length > 1 ? p : "/";
    }

    private static string RaceLabel(Job job) =>
        string.IsNullOrWhiteSpace(job.Request.Label) ? job.Id : job.Request.Label;

    private static RaceActionResult Fail(RaceActionResult result, string message)
    {
        result.Ok = false;
        result.Lines.Add(message);
        return result;
    }

    private static void Merge(RaceActionResult into, RaceActionResult part)
    {
        into.Ok &= part.Ok;
        into.Lines.AddRange(part.Lines);
    }
}
