using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

public sealed partial class WeaveEngine
{
    private sealed record CompletionSnapshot(bool Complete, string Identity, int Total, int Covered,
        long Bytes, List<RaceSfvFile> Manifest);

    private sealed record CompletionSite(string Name, Site Site, string Path, List<SkiplistEntry> Skiplist,
        Dictionary<string, RaceFile> Files, Dictionary<string, string> Manifest, bool MarkerComplete);

    private readonly ConcurrentDictionary<string, byte> _raceRechecks = new(StringComparer.OrdinalIgnoreCase);

    // This is deliberately separate from RetryJob: no workers or FTP writes are started.
    public async Task<bool> RecheckRaceAsync(string id, CancellationToken ct = default)
    {
        var before = _store.Job(id);
        if (before is not { Type: JobType.Race, State: JobState.Failed } || before.Request.DryRun) return false;
        if (!_raceRechecks.TryAdd(id, 0)) return false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var first = await ReadRaceCompletionAsync(before.Request, before, budget.Token).ConfigureAwait(false);
            if (!first.Complete) return false;
            await Task.Delay(750, budget.Token).ConfigureAwait(false);
            var second = await ReadRaceCompletionAsync(before.Request, before, budget.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!second.Complete || first.Identity != second.Identity) return false;
            var changed = false;
            lock (_jobRunLock)
            {
                if (_jobRuns.ContainsKey(id)) return false;
                _store.UpdateJob(id, job =>
                {
                    if (job.State != JobState.Failed || job.FinishedAt != before.FinishedAt ||
                        job.StartedAt != before.StartedAt) return;
                    ApplyCompletionSnapshot(job, second);
                    job.State = JobState.Succeeded;
                    job.Error = "";
                    // Preserve original end time and transfer history; record when verification happened separately.
                    job.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "info",
                        Message = "read-only completion recheck: fresh stable SFV/listings confirm release complete; previous failure retained in history" });
                    changed = true;
                });
            }
            if (changed) NotifyChanged();
            return changed;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException) { return false; }
        finally { _raceRechecks.TryRemove(id, out _); }
    }

    private async Task<bool> WaitForRaceCompletionAsync(string id, TransferRequest req, int seconds, CancellationToken ct)
    {
        var original = _store.Job(id)!;
        if (!_jobRuns.TryGetValue(id, out var run)) return false;
        // Without an SFV, the regular quiet-completion algorithm remains authoritative.
        if (original.SfvFiles.Count == 0) return false;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        string? previous = null;
        var finalProbeUsed = false;
        if (seconds > 0)
        {
            ClearProgress(id);
            _store.UpdateJobTransient(id, j => { j.SpeedBps = 0; j.CurrentFile = ""; j.Slots.Clear(); });
            LogJob(id, "info", $"read-only completion recheck for up to {seconds}s; no new upload attempts");
        }
        do
        {
            ct.ThrowIfCancellationRequested();
            await WaitWhilePausedAsync(id, ct).ConfigureAwait(false);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(ct);
            probe.CancelAfter(TimeSpan.FromSeconds(Math.Max(3, Math.Min(15, (deadline - DateTime.UtcNow).TotalSeconds))));
            try
            {
                var snapshot = await ReadRaceCompletionAsync(req, original, probe.Token).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (snapshot.Complete && previous == snapshot.Identity)
                {
                    lock (_jobRunLock)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!IsCurrentJobRun(id, run)) return false;
                        _store.UpdateJobTransient(id, job => { if (!job.Terminal) ApplyCompletionSnapshot(job, snapshot); });
                    }
                    LogJob(id, "info", "read-only completion recheck: fresh stable SFV/listings confirm release complete");
                    return true;
                }
                previous = snapshot.Complete ? snapshot.Identity : null;
                lock (_jobRunLock)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsCurrentJobRun(id, run)) return false;
                    _store.UpdateJobTransient(id, job => { if (!job.Terminal) ApplyCompletionSnapshot(job, snapshot); });
                }
                NotifyChanged();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { previous = null; }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException) { previous = null; }
            // A potentially complete first snapshot always gets a second, independent observation.
            if (DateTime.UtcNow >= deadline)
            {
                if (previous is null || finalProbeUsed) break;
                finalProbeUsed = true;
            }
            var delayMs = previous is not null ? 750 : Math.Min(5000, Math.Max(1, (deadline - DateTime.UtcNow).TotalMilliseconds));
            await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
        } while (DateTime.UtcNow < deadline || previous is not null);
        if (seconds > 0) LogJob(id, "warn", "read-only completion recheck expired without stable complete evidence");
        return false;
    }

    private async Task<CompletionSnapshot> ReadRaceCompletionAsync(TransferRequest req, Job original, CancellationToken ct)
    {
        var settings = _store.Settings();
        var mesh = req.MeshSites.Count > 1;
        var topologyVersion = Interlocked.Read(ref _raceRouteVersion);
        var names = (mesh ? req.MeshSites : new List<string> { req.FromSite, req.ToSite })
            .Where(name => !IsRemovedFromRace(original.Id, name))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool Route(string from, string to) => !from.Equals(to, StringComparison.OrdinalIgnoreCase) &&
            !IsRemovedFromRace(original.Id, from) && !IsRemovedFromRace(original.Id, to) &&
            (mesh || (from.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) && to.Equals(req.ToSite, StringComparison.OrdinalIgnoreCase))) &&
            !req.DownloadOnlySites.Contains(to, StringComparer.OrdinalIgnoreCase) && _store.TransferAllowed(from, to, race: true);
        var routes = names.SelectMany(from => names.Where(to => Route(from, to)).Select(to => (From: from, To: to))).ToArray();
        if (routes.Length == 0) throw new IOException("no permitted race destinations to verify");
        var sites = new List<CompletionSite>();
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested();
            var site = _store.Site(name) ?? throw new IOException("race site no longer exists");
            var path = mesh && req.SitePaths.TryGetValue(name, out var mapped) && !string.IsNullOrWhiteSpace(mapped)
                ? mapped : name.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) ? req.SourcePath : req.DestPath;
            var skip = MergeSkiplists(settings.GlobalSkiplist, site.Skiplist);
            var pool = AcquirePool(name, site, FtpConfig(site, name, false));
            FtpClient? conn = null;
            try
            {
                conn = await pool.BorrowAsync(ct).ConfigureAwait(false);
                var listed = await ListSourceFilesAsync(conn, path, skip, ct, completeMarkers: CompleteMarkersFor(site),
                    throwOnAnyFailure: true).ConfigureAwait(false);
                var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var sfv in listed.Where(f => f.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)))
                {
                    var raw = await conn.RetrieveTextAsync(sfv.Abs, 1024 * 1024, ct).ConfigureAwait(false);
                    var entries = Sfv.Parse(raw);
                    if (entries.Count == 0) throw new IOException("SFV is empty or unreadable");
                    foreach (var entry in entries)
                    {
                        if (entry.Crc.Length != 8 || !uint.TryParse(entry.Crc, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                            entry.Name.StartsWith('/') || entry.Name.Contains('\\') || entry.Name.Split('/').Any(p => p is ".." or "." or ""))
                            throw new IOException("invalid SFV entry");
                        var rel = sfv.ParentRel.Length == 0 ? entry.Name : sfv.ParentRel + "/" + entry.Name;
                        if (manifest.TryGetValue(rel, out var crc) && !crc.Equals(entry.Crc, StringComparison.OrdinalIgnoreCase))
                            throw new IOException("conflicting SFV entries");
                        manifest[rel] = entry.Crc.ToUpperInvariant();
                    }
                }
                // RETR and LIST are separate observations. Never decide using the pre-RETR listing.
                var fresh = await ListSourceFilesAsync(conn, path, skip, ct, completeMarkers: CompleteMarkersFor(site),
                    throwOnAnyFailure: true).ConfigureAwait(false);
                if (!listed.Select(f => (f.Rel, f.Size)).OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(fresh.Select(f => (f.Rel, f.Size)).OrderBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)))
                    throw new IOException("listing changed during completion verification");
                var root = await conn.ListAsync(path, ct).ConfigureAwait(false);
                var markerComplete = root.Any(e => CompleteMarkersFor(site).Any(m => CompletionMarkerMatches(e.Name, m)));
                sites.Add(new(name, site, path, skip, fresh.ToDictionary(f => f.Rel, StringComparer.OrdinalIgnoreCase), manifest, markerComplete));
                pool.Return(conn);
                conn = null;
            }
            finally
            {
                if (conn is not null) pool.Drop(conn);
                ReleasePool(name);
            }
        }

        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in sites.SelectMany(s => s.Manifest))
        {
            if (expected.TryGetValue(entry.Key, out var crc) && crc != entry.Value) throw new IOException("source/destination SFVs disagree");
            expected[entry.Key] = entry.Value;
        }
        if (expected.Count == 0) throw new IOException("no fresh SFV evidence");
        foreach (var saved in original.SfvFiles)
            if (!expected.TryGetValue(saved.Name, out var crc) || !crc.Equals(saved.Crc, StringComparison.OrdinalIgnoreCase))
                throw new IOException("fresh SFV does not contain the original manifest");

        var known = sites.SelectMany(s => s.Files.Keys).Concat(expected.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var targets = sites.Where(s => routes.Any(r => r.To.Equals(s.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        var rows = new List<RaceSfvFile>();
        var covered = 0;
        long totalBytes = 0;
        foreach (var name in known)
        {
            var sources = sites.Where(s => routes.Any(r => r.From.Equals(s.Name, StringComparison.OrdinalIgnoreCase)));
            var size = sources.Where(s => s.Files.ContainsKey(name)).Select(s => s.Files[name].Size).DefaultIfEmpty(0).Max();
            var saved = original.SfvFiles.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (size <= 0 && saved?.Size is > 0) size = saved.Size.Value;
            // Preserve coverage of externally supplied files with no source-size hint.
            // This presence alone cannot complete reconciliation: stable SFVs and
            // root completion markers on every destination are still required.
            if (size <= 0) size = sites.Where(s => s.Files.ContainsKey(name)).Select(s => s.Files[name].Size).DefaultIfEmpty(0).Max();
            var ok = size > 0 && targets.All(dst =>
                SkiplistRules.Blocks(dst.Skiplist, name) ||
                SkiplistRules.UniqueSatisfied(dst.Skiplist, name, dst.Files.Keys, new SkiplistUniqueState(), dst.Name) ||
                (dst.Files.TryGetValue(name, out var file) && file.Size == size &&
                 (!expected.ContainsKey(name) || (dst.Manifest.TryGetValue(name, out var crc) && crc == expected[name]))));
            if (size == 1 && expected.TryGetValue(name, out var checksum) && SfvExcludesSingleByte(checksum)) ok = false;
            if (ok) covered++;
            totalBytes += size;
            if (expected.TryGetValue(name, out var fileCrc))
                rows.Add(new RaceSfvFile { Name = name, Crc = fileCrc, Sfv = saved?.Sfv ?? "", Size = size > 0 ? size : null,
                    Status = ok ? "covered" : size > 0 ? "waiting" : "missing",
                    UploadedSize = saved?.UploadedSize, UploadedByUs = saved?.UploadedByUs == true && saved.UploadedSize == size });
        }
        // Include every site's files, manifest and current routes, not only aggregate counters.
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            routes = routes.Select(r => new { r.From, r.To }),
            sites = sites.Select(s => new { s.Name, s.Path, s.MarkerComplete, config = CompletionSiteIdentity(s.Site),
                files = s.Files.Values.OrderBy(f => f.Rel).Select(f => new { f.Rel, f.Size }),
                manifest = s.Manifest.OrderBy(f => f.Key) })
        }))));
        if (topologyVersion != Interlocked.Read(ref _raceRouteVersion) ||
            !routes.SequenceEqual(names.SelectMany(from => names.Where(to => Route(from, to)).Select(to => (From: from, To: to)))))
            throw new IOException("race policy changed during completion verification");
        if (sites.Any(s => _store.Site(s.Name) is not { } current || CompletionSiteIdentity(s.Site) != CompletionSiteIdentity(current)))
            throw new IOException("site configuration changed during completion verification");
        // Equal sizes do not disprove a CRC failure. Late reconciliation also needs
        // the destination's fresh root completion signal, never a nested sample marker.
        return new(covered == known.Length && targets.All(s => s.MarkerComplete), identity, known.Length, covered, totalBytes, rows);
    }

    private static void ApplyCompletionSnapshot(Job job, CompletionSnapshot snapshot)
    {
        job.RaceCoverageKnown = true;
        job.FilesTotal = snapshot.Total;
        job.FilesCovered = snapshot.Covered;
        job.BytesTotal = snapshot.Bytes;
        job.SfvFiles = snapshot.Manifest;
        // FilesDone/BytesDone and attempt history describe our contribution, not opponent coverage.
    }

    private static string CompletionSiteIdentity(Site site) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(site)));
}
