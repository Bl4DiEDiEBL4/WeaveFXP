using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text.RegularExpressions;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

/// <summary>
/// Top-level engine: owns the store, runs FXP/download jobs, keeps an in-memory activity
/// log (FTP control channel, transfer events, system), and exposes site operations.
/// Registered as a singleton in the Blazor host.
/// </summary>
public sealed partial class WeaveEngine
{
    private const int MaxLogEntries = 3000;
    private const int MaxJobEvents = 1000;

    // Reserve capacity only while another race actually has pending work.
    private const int NewcomerReservedSlots = 2;

    private readonly JsonStore _store;
    private readonly object _routePerformanceLock = new();
    private readonly Dictionary<string, RoutePerformance> _routePerformance = new(StringComparer.OrdinalIgnoreCase);
    private readonly GlobalMeshScoreboard<MeshPick> _meshScoreboard = new();
    private readonly object _logLock = new();
    private long _logSeq;
    private readonly List<LogEntry> _logRing = new();
    private readonly List<LogEntry> _pendingLogs = new();
    private readonly object _logPersistenceLock = new();
    private readonly System.Threading.Timer _logFlushTimer;

    // Raised whenever the log or a job changes, so the UI can refresh live.
    public event Action? Changed;

    public WeaveEngine(string? statePath = null)
    {
        _store = new JsonStore(string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath() : statePath!);
        SeedRoutePerformance();
        try
        {
            _logRing.AddRange(_store.StoredLogs(MaxLogEntries));
            if (_logRing.Count > 0) _logSeq = _logRing[^1].Seq;
        }
        catch { /* logging remains available in memory if SQLite cannot be read */ }
        _logFlushTimer = new System.Threading.Timer(_ => FlushPendingLogs(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushPendingLogs();
        Console.CancelKeyPress += (_, _) => FlushPendingLogs();
        var interrupted = _store.FailInterruptedJobs("WeaveFXP restarted before this job finished");
        if (interrupted > 0)
            Log("system", "startup", "warn", $"marked {interrupted} interrupted running job(s) as failed");
        // Restore learned FXP TLS orientations so we never re-pay a 30s handshake
        // timeout rediscovering them after a restart.
        FxpTransfer.SeedRoleFlips(_store.Settings().FxpTlsRoleFlip);
        FxpTransfer.RoleFlipLearned = (pair, flip) =>
        {
            try
            {
                var s = _store.Settings();
                s.FxpTlsRoleFlip[pair] = flip;
                _store.UpdateSettings(s);
            }
            catch { }
        };
        // Keep the per-site connection pools warm BETWEEN races (cbftp keeps its site
        // slots permanently logged in): NOOP idle conns so the daemon doesn't kick
        // them and replace dead sessions. This is what lets the first STOR of
        // an announce fire in milliseconds instead of after a fresh TCP+TLS+login.
        _poolSweepTimer = new System.Threading.Timer(_ => { _ = SweepPoolsAsync(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        _ = Task.Run(PrimeSitePoolsAsync);
    }

    private readonly System.Threading.Timer? _poolSweepTimer;

    private sealed class RoutePerformance
    {
        public double EwmaBps;
        public int Samples;
    }

    private void SeedRoutePerformance()
    {
        try
        {
            foreach (var row in _store.Jobs()
                .SelectMany(job => job.Files)
                .Where(row => row.Status == "done" && row.Bps > 0 && row.Size >= 1024 * 1024 &&
                    !string.IsNullOrWhiteSpace(row.FromSite) && !string.IsNullOrWhiteSpace(row.ToSite))
                .OrderBy(row => row.StartedAt))
                RecordRoutePerformance(row.FromSite, row.ToSite, row.Bps);
        }
        catch { /* route learning starts empty when history cannot be read */ }
    }

    private void RecordRoutePerformance(string fromSite, string toSite, double bps)
    {
        if (!double.IsFinite(bps) || bps < 1024 || bps > 4L * 1024 * 1024 * 1024) return;
        var key = fromSite + ">" + toSite;
        lock (_routePerformanceLock)
        {
            if (!_routePerformance.TryGetValue(key, out var route))
            {
                route = new RoutePerformance();
                _routePerformance[key] = route;
            }
            route.EwmaBps = route.Samples == 0 ? bps : route.EwmaBps * 0.75 + bps * 0.25;
            route.Samples++;
        }
    }

    private long RoutePerformanceScore(string fromSite, string toSite)
    {
        lock (_routePerformanceLock)
        {
            if (!_routePerformance.TryGetValue(fromSite + ">" + toSite, out var route) || route.Samples == 0)
                return 0;
            // Tie-breaker within the same file priority/size. Keep it below one file-size
            // point so protocol-critical ordering remains dominant.
            return Math.Clamp((long)(route.EwmaBps / 1024), 1, 999_999);
        }
    }

    private async Task SweepPoolsAsync()
    {
        List<SitePool> pools;
        lock (_poolLock) pools = _pools.Values.ToList();
        foreach (var pool in pools)
        {
            try { await pool.SweepAsync(TimeSpan.MaxValue, TimeSpan.FromSeconds(45)).ConfigureAwait(false); }
            catch { /* best-effort keepalive */ }
        }
    }

    private async Task PrimeSitePoolsAsync()
    {
        var sites = _store.Sites();
        await Task.WhenAll(sites.Select(PrimeSitePoolAsync)).ConfigureAwait(false);
    }

    private async Task PrimeSitePoolAsync(Site site)
    {
        SitePool? pool = null;
        try
        {
            pool = AcquirePool(site.Name, site, FtpConfig(site, site.Name, false));
            var slots = Math.Max(
                ResolveSiteSlots(site.DownloadSlots, site),
                ResolveSiteSlots(site.UploadSlots, site));
            var target = Math.Min(pool.Max - 1, Math.Max(2, slots + 1));
            await pool.WarmUpAsync(target, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log("system", site.Name, "warn", $"connection prewarm failed: {FirstLineOf(ex.Message)}");
        }
        finally
        {
            if (pool is not null) ReleasePool(site.Name);
        }
    }

    public static string DefaultStatePath()
    {
        var overridePath = Environment.GetEnvironmentVariable("WEAVEFXP_STATE");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath!;
        var exe = Environment.ProcessPath;
        var dir = string.IsNullOrEmpty(exe) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(exe)!;
        return Path.Combine(dir, "data", "state.json");
    }

    public string StatePath => _store.Path;
    public string DataDir => Path.GetDirectoryName(StatePath) ?? Directory.GetCurrentDirectory();
    public string LoadWarning => _store.LoadWarning;
    public string Version
    {
        get
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
                ?? "1.0.2";
            var metadata = version.IndexOf('+');
            return metadata < 0 ? version : version[..metadata];
        }
    }

    private void NotifyChanged() => Changed?.Invoke();

    // Coalesced UI notification. Protocol chatter can fire hundreds of times a second;
    // two UI frames per second keeps live data useful without repeatedly diffing large
    // race/log tables while the engine is busy.
    private long _lastNotifyTicks;
    private int _notifyPending;
    private void NotifyChangedThrottled()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastNotifyTicks);
        if (now - last >= TimeSpan.TicksPerMillisecond * 500)
        {
            Interlocked.Exchange(ref _lastNotifyTicks, now);
            NotifyChanged();
            return;
        }
        if (Interlocked.CompareExchange(ref _notifyPending, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(500).ConfigureAwait(false);
            Interlocked.Exchange(ref _notifyPending, 0);
            Interlocked.Exchange(ref _lastNotifyTicks, DateTime.UtcNow.Ticks);
            NotifyChanged();
        });
    }

    // ---- live progress ----------------------------------------------------------------
    // Speed is measured over a short sliding window per job. Progress mutates the job in
    // memory only (no state.json write) and the Changed event to the UI is throttled.
    private sealed class SpeedWindow
    {
        public DateTime Start = DateTime.UtcNow;
        public long StartBytes;
    }
    private readonly Dictionary<string, SpeedWindow> _speed = new();

    private void ReportProgress(string id, long fileBytes, long fileTotal, long cumulativeBytes, string currentFile)
    {
        SpeedWindow win;
        lock (_speed)
        {
            if (!_speed.TryGetValue(id, out win!))
            {
                win = new SpeedWindow { StartBytes = cumulativeBytes };
                _speed[id] = win;
            }
        }
        var now = DateTime.UtcNow;
        var elapsed = (now - win.Start).TotalSeconds;
        double speed = elapsed > 0.001 ? (cumulativeBytes - win.StartBytes) / elapsed : 0;
        // Slide the window so speed reflects recent throughput, not the whole transfer.
        if (elapsed > 1.5)
        {
            win.Start = now;
            win.StartBytes = cumulativeBytes;
        }

        _store.UpdateJobTransient(id, j =>
        {
            j.BytesDone = fileBytes;
            j.BytesTotal = fileTotal;
            j.CumulativeBytes = cumulativeBytes;
            j.SpeedBps = speed;
            j.CurrentFile = currentFile;
        });

        // Coalesce progress from every concurrent job into one global UI cadence.
        // Per-job throttling multiplied the render rate by the number of active races.
        NotifyChangedThrottled();
    }

    private void ClearProgress(string id)
    {
        lock (_speed) _speed.Remove(id);
        _store.UpdateJobTransient(id, j => { j.Slots = new List<SlotProgress>(); j.SpeedBps = 0; });
    }

    // IProgress<T> that invokes its handler synchronously on the reporting thread,
    // so progress stays ordered and drains before the transfer call returns (unlike
    // Progress<T>, which posts to a captured context / the thread pool).
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    // ---- logging ----------------------------------------------------------------------

    public void Log(string category, string site, string level, string message)
    {
        lock (_logLock)
        {
            _logSeq++;
            var entry = new LogEntry
            {
                Seq = _logSeq,
                Time = DateTime.UtcNow,
                Category = category,
                Site = site,
                Level = level,
                Message = message,
            };
            _logRing.Add(entry);
            _pendingLogs.Add(entry);
            if (_logRing.Count > MaxLogEntries)
                _logRing.RemoveRange(0, _logRing.Count - MaxLogEntries);
            if (_pendingLogs.Count > MaxLogEntries)
                _pendingLogs.RemoveRange(0, _pendingLogs.Count - MaxLogEntries);
        }
        NotifyChangedThrottled();
    }

    private void FlushPendingLogs()
    {
        lock (_logPersistenceLock)
        {
            List<LogEntry> batch;
            lock (_logLock)
            {
                if (_pendingLogs.Count == 0) return;
                batch = _pendingLogs.ToList();
                _pendingLogs.Clear();
            }

            try
            {
                _store.AppendLogs(batch, MaxLogEntries);
            }
            catch
            {
                // Keep a bounded retry buffer. Logging itself must never stall a race.
                lock (_logLock)
                {
                    _pendingLogs.InsertRange(0, batch);
                    if (_pendingLogs.Count > MaxLogEntries)
                        _pendingLogs.RemoveRange(0, _pendingLogs.Count - MaxLogEntries);
                }
            }
        }
    }

    private int ClearLogHistory()
    {
        lock (_logPersistenceLock)
        {
            int memoryCount;
            lock (_logLock)
            {
                memoryCount = _logRing.Count;
                _logRing.Clear();
                _pendingLogs.Clear();
                _logSeq++;
            }
            var storedCount = 0;
            try { storedCount = _store.ClearStoredLogs(); } catch { }
            return Math.Max(memoryCount, storedCount);
        }
    }

    public (List<LogEntry> entries, long seq) Logs(long after, int limit = 500)
    {
        if (limit <= 0 || limit > MaxLogEntries) limit = MaxLogEntries;
        lock (_logLock)
        {
            var list = _logRing.Where(e => e.Seq > after).ToList();
            if (list.Count > limit) list = list.Skip(list.Count - limit).ToList();
            var seq = list.Count > 0 ? list[^1].Seq : after;
            return (list, seq);
        }
    }

    public List<LogEntry> RecentLogs(int limit = 1000, string category = "", string level = "")
    {
        if (limit <= 0 || limit > MaxLogEntries) limit = MaxLogEntries;
        lock (_logLock)
        {
            IEnumerable<LogEntry> q = _logRing;
            if (!string.IsNullOrWhiteSpace(category))
                q = q.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(level))
                q = q.Where(e => e.Level.Equals(level, StringComparison.OrdinalIgnoreCase));
            return q.OrderByDescending(e => e.Seq).Take(limit).Select(e => new LogEntry
            {
                Seq = e.Seq,
                Time = e.Time,
                Category = e.Category,
                Site = e.Site,
                Level = e.Level,
                Message = e.Message,
            }).ToList();
        }
    }

    // verbose: log every FTP command/response for this connection. On for interactive
    // work (browsing, manual transfers) so the FTP Log stays useful; off for
    // API-triggered races where the logging cost is throughput. The FtpDebugLog
    // setting forces it on everywhere.
    private FtpClient.Config FtpConfig(Site site, string logAlias = "", bool verbose = true)
    {
        var settings = _store.Settings();
        var cfg = FtpClient.Config.FromSite(site);
        var name = string.IsNullOrWhiteSpace(logAlias) ? site.Name : logAlias.Trim();
        cfg.SkipEmptyFolders = settings.SkipEmptyFolders;
        cfg.Skiplist = MergePatternLists(settings.GlobalSkiplist, site.Skiplist);
        cfg.OrderList = MergePatternLists(settings.GlobalOrderList);
        cfg.CwdBeforeStatListing = !string.IsNullOrWhiteSpace(logAlias);
        cfg.TcpSendBufferKBytes = settings.TcpSendBufferKBytes;
        cfg.TcpReceiveBufferKBytes = settings.TcpReceiveBufferKBytes;
        cfg.Proxy = settings.Proxy;
        cfg.ProxyUsername = settings.ProxyUsername;
        cfg.ProxyPassword = settings.ProxyPassword;
        cfg.DataProxy = settings.DataProxy;
        cfg.DataProxyUsername = settings.DataProxyUsername;
        cfg.DataProxyPassword = settings.DataProxyPassword;
        cfg.Trace = verbose || settings.FtpDebugLog ? line => Log("ftp", name, "info", line) : null;
        return cfg;
    }

    // ---- settings ---------------------------------------------------------------------

    public AppSettings Settings(bool pub)
    {
        var settings = _store.Settings();
        return pub ? settings.Public() : settings;
    }

    public AppSettings UpdateSettings(AppSettings settings)
    {
        var current = _store.Settings();
        if (string.IsNullOrWhiteSpace(settings.ApiPassword)) settings.ApiPassword = current.ApiPassword;
        if (string.IsNullOrWhiteSpace(settings.ProxyPassword)) settings.ProxyPassword = current.ProxyPassword;
        if (string.IsNullOrWhiteSpace(settings.DataProxyPassword)) settings.DataProxyPassword = current.DataProxyPassword;
        if (settings.CreatedAt == default) settings.CreatedAt = current.CreatedAt;
        var saved = _store.UpdateSettings(settings);
        Log("system", "settings", "info", "settings saved");
        _ = Task.Run(PrimeSitePoolsAsync);
        NotifyChanged();
        return saved;
    }

    // ---- sites ------------------------------------------------------------------------

    public Site AddSite(Site site)
    {
        if (string.IsNullOrWhiteSpace(site.Password))
        {
            var existing = _store.Site(site.Name);
            if (existing is not null) site.Password = existing.Password;
        }
        var saved = _store.UpsertSite(site);
        Log("system", saved.Name, "info", $"site {saved.Name} saved ({saved.Host}:{saved.Port})");
        _ = Task.Run(() => PrimeSitePoolAsync(saved));
        return saved;
    }

    public Site SaveSite(string? originalName, Site site)
    {
        if (string.IsNullOrWhiteSpace(originalName))
            return AddSite(site);

        if (string.IsNullOrWhiteSpace(site.Password))
        {
            var existing = _store.Site(originalName);
            if (existing is not null) site.Password = existing.Password;
        }

        var saved = _store.SaveSite(originalName, site);
        var renamed = !saved.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase);
        Log("system", saved.Name, "info", renamed
            ? $"site {originalName} renamed to {saved.Name} ({saved.Host}:{saved.Port})"
            : $"site {saved.Name} saved ({saved.Host}:{saved.Port})");
        _ = Task.Run(() => PrimeSitePoolAsync(saved));
        return saved;
    }

    public void RemoveSite(string name)
    {
        if (_store.DeleteSite(name)) Log("system", name, "info", $"site {name} deleted");
    }

    public Site? Site(string name) => _store.Site(name);

    public List<Site> Sites(bool pub)
    {
        var sites = ApplySiteOrder(_store.Sites(), _store.Settings().SiteOrder);
        return pub ? sites.Select(s => s.Public()).ToList() : sites;
    }

    public List<Job> Jobs() => _store.Jobs();
    public List<Job> HistoryJobs(int archiveLimit = 10000) => _store.HistoryJobs(archiveLimit);
    public int ArchivedJobCount() => _store.ArchivedJobCount();
    public Job? Job(string id) => _store.Job(id);
    public List<ReleaseCheck> Releases() => _store.Releases();
    public int DupeCount() => _store.DupeCount();

    public bool CancelJob(string id, string reason = "Cancelled by user")
    {
        var job = _store.Job(id);
        if (job is null || job.Terminal) return false;
        CancelJobInternal(id, reason);
        return true;
    }

    public int CancelLocalTransfersForSite(string site, string reason = "Disconnected from browser pane")
    {
        site = (site ?? "").Trim();
        if (site.Length == 0 || site.Equals("local", StringComparison.OrdinalIgnoreCase)) return 0;

        var jobs = _store.Jobs()
            .Where(job => job.State is JobState.Queued or JobState.Running)
            .Where(job =>
                (job.Type == JobType.Download &&
                 job.Request.FromSite.Equals(site, StringComparison.OrdinalIgnoreCase) &&
                 job.Request.ToSite.Equals("local", StringComparison.OrdinalIgnoreCase)) ||
                (job.Type == JobType.Upload &&
                 job.Request.FromSite.Equals("local", StringComparison.OrdinalIgnoreCase) &&
                 job.Request.ToSite.Equals(site, StringComparison.OrdinalIgnoreCase)))
            .Select(job => job.Id)
            .ToList();

        foreach (var id in jobs)
            CancelJobInternal(id, reason);
        return jobs.Count;
    }

    public bool RemoveJob(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return false;
        var job = _store.Job(id);
        if (job is { Terminal: false })
            CancelJobInternal(id, "Removed from queue");
        var removed = _store.DeleteJob(id);
        if (removed)
        {
            Log("system", "jobs", "info", $"removed {id}");
            NotifyChanged();
        }
        return removed;
    }

    public Dictionary<string, int> ManualQueuePositions() => _manualTransferQueue.Positions();

    public bool CanMoveManualJob(string id, int direction) => _manualTransferQueue.CanMove(id, direction);

    public bool MoveManualJob(string id, int direction)
    {
        if (!_manualTransferQueue.Move(id, direction)) return false;
        NotifyChanged();
        return true;
    }

    public int RemoveManualJobs(IEnumerable<string> ids)
    {
        using var suspended = _manualTransferQueue.SuspendDispatch();
        var removed = 0;
        foreach (var id in ids.Distinct(StringComparer.OrdinalIgnoreCase))
            if (_store.Job(id) is { Type: not JobType.Race } && RemoveJob(id)) removed++;
        return removed;
    }

    public bool RetryJob(string id)
    {
        var existing = _store.Job(id);
        if (existing is null || !existing.Terminal) return false;

        if (string.IsNullOrWhiteSpace(existing.Request.FromSite) ||
            string.IsNullOrWhiteSpace(existing.Request.SourcePath))
            return false;
        if (existing.Type is not (JobType.Download or JobType.Upload) && string.IsNullOrWhiteSpace(existing.Request.ToSite))
            return false;

        // Install the new run generation before resetting the row. If a cancelled old
        // task is still unwinding, it can no longer finish/cancel this retry.
        var run = RegisterJobToken(id);
        // Reuse the SAME job row: reset live counters and rerun, instead of spawning a
        // new line in the list. Keep the per-file transfer history so retries do not
        // erase what already completed/failed in the previous attempt.
        var reset = _store.UpdateJob(id, j =>
        {
            j.State = JobState.Queued;
            j.Error = "";
            j.StartedAt = default;
            j.FinishedAt = default;
            j.Paused = false;
            j.BytesDone = 0; j.BytesTotal = 0; j.CumulativeBytes = 0; j.SpeedBps = 0;
            j.FilesDone = 0; j.FilesCovered = 0; j.FilesTotal = 0; j.CurrentFile = "";
            j.Slots = new List<SlotProgress>();
            j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "info", Message = "— retry: job restarted —" });
        });
        if (reset is null)
        {
            UnregisterJobToken(id, run);
            return false;
        }
        NotifyChanged();

        if (existing.Type == JobType.Download)
        {
            var req = new DownloadRequest
            {
                Site = existing.Request.FromSite,
                SourcePath = existing.Request.SourcePath,
                DestPath = existing.Request.DestPath,
                Label = existing.Request.Label,
                ViaApi = existing.Request.ViaApi,
            };
            ScheduleDownload(id, req, run);
            return true;
        }

        if (existing.Type == JobType.Upload)
        {
            var req = new UploadRequest
            {
                Site = existing.Request.ToSite,
                SourcePath = existing.Request.SourcePath,
                DestPath = existing.Request.DestPath,
                Label = existing.Request.Label,
                ViaApi = existing.Request.ViaApi,
            };
            ScheduleUpload(id, req, run);
            return true;
        }

        _ = ScheduleTransfer(id, existing.Request, run);
        return true;
    }

    public bool RestartJob(string id)
    {
        var existing = _store.Job(id);
        if (existing is null) return false;
        if (!existing.Terminal) CancelJobInternal(id, "Restart requested");
        return RetryJob(id);
    }

    public int ClearLogs()
    {
        var count = ClearLogHistory();
        Log("system", "maintenance", "warn", $"cleared {count} log entr{(count == 1 ? "y" : "ies")}");
        return count;
    }

    public int ClearJobs()
    {
        var count = _store.ClearJobs();
        Log("system", "maintenance", "warn", $"cleared {count} transfer job(s)");
        NotifyChanged();
        return count;
    }

    public int ClearReleases()
    {
        var count = _store.ClearReleases();
        Log("system", "maintenance", "warn", $"cleared {count} release check(s)");
        NotifyChanged();
        return count;
    }

    public int ClearDupes()
    {
        var count = _store.ClearDupes();
        Log("system", "maintenance", "warn", $"cleared {count} dupe result(s)");
        NotifyChanged();
        return count;
    }

    public DataMaintenanceResult ClearRuntimeData()
    {
        var result = new DataMaintenanceResult
        {
            Jobs = _store.ClearJobs(),
            Releases = _store.ClearReleases(),
            Dupes = _store.ClearDupes(),
        };
        result.Logs = ClearLogHistory();
        Log("system", "maintenance", "warn",
            $"cleared runtime data: {result.Jobs} job(s), {result.Releases} release check(s), {result.Dupes} dupe result(s), {result.Logs} log entr{(result.Logs == 1 ? "y" : "ies")}");
        NotifyChanged();
        return result;
    }

    // ---- remote listing / probe / dupe / release --------------------------------------

    public Task<List<RemoteEntry>> ListRemoteAsync(string siteName, string path, CancellationToken ct = default) =>
        ListRemoteAsync(siteName, path, "", ct);

    public async Task<List<RemoteEntry>> ListRemoteAsync(string siteName, string path, string logAlias, CancellationToken ct = default)
    {
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site, logAlias), ct).ConfigureAwait(false);
        return await client.ListAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<byte[]> RetrieveRemoteFileAsync(string siteName, string path, long maxBytes = 15 * 1024 * 1024, CancellationToken ct = default)
    {
        siteName = (siteName ?? "").Trim();
        path = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentException("site is required");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site), ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        await client.RetrieveToAsync(path, ms, ct, maxBytes: maxBytes).ConfigureAwait(false);
        return ms.ToArray();
    }

    public async Task DeleteRemotePathAsync(string siteName, string path, CancellationToken ct = default)
    {
        siteName = (siteName ?? "").Trim();
        path = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentException("site is required");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
        if (path is "/" or "\\" || path.Trim().Length <= 3 && path.Trim().StartsWith("/", StringComparison.Ordinal))
            throw new ArgumentException("refusing to delete remote root");
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site), ct).ConfigureAwait(false);
        await DeleteRemoteTreeAsync(client, path, ct).ConfigureAwait(false);
        Log("system", siteName, "warn", $"deleted remote path {path}");
    }

    private static async Task DeleteRemoteTreeAsync(FtpClient client, string path, CancellationToken ct)
    {
        var (deleteCode, deleteMsg) = await client.CommandAsync("DELE " + path).ConfigureAwait(false);
        if (deleteCode / 100 == 2)
            return;

        try
        {
            var entries = await client.ListAsync(path, ct).ConfigureAwait(false);
            foreach (var entry in entries)
            {
                if (entry.Name is "." or "..") continue;
                var child = FtpClient.JoinRemote(path, entry.Name);
                if (entry.Type is "dir" or "link")
                    await DeleteRemoteTreeAsync(client, child, ct).ConfigureAwait(false);
                else
                {
                    var (childCode, _) = await client.CommandAsync("DELE " + child).ConfigureAwait(false);
                    if (childCode / 100 != 2)
                        await DeleteRemoteTreeAsync(client, child, ct).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            // Some servers refuse LIST on special dirs; RMD below will surface the useful error.
        }

        var (removeCode, removeMsg) = await client.CommandAsync("RMD " + path).ConfigureAwait(false);
        if (removeCode / 100 == 2)
            return;

        throw new IOException($"delete {path} failed: DELE {deleteCode} {deleteMsg}; RMD {removeCode} {removeMsg}");
    }

    public async Task<SiteProbe> ProbeSiteAsync(string siteName, CancellationToken ct = default)
    {
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site), ct).ConfigureAwait(false);
        var probe = new SiteProbe { Site = siteName, CheckedAt = DateTime.UtcNow };

        async Task Run(string command)
        {
            int code;
            string msg;
            try
            {
                (code, msg) = await client.CommandAsync(command).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                probe.Results.Add(new ProbeCommandResult { Command = command, Message = ex.Message });
                return;
            }
            var result = new ProbeCommandResult
            {
                Command = command,
                Code = code,
                Message = msg.Trim(),
                Ok = code / 100 == 2,
            };
            if (command.Equals("FEAT", StringComparison.OrdinalIgnoreCase) && result.Ok)
                probe.Features = ParseFeatures(msg);
            probe.Results.Add(result);
        }

        await Run("SYST").ConfigureAwait(false);
        await Run("FEAT").ConfigureAwait(false);
        await Run("SITE VERS").ConfigureAwait(false);
        if (site.UsePret) await Run("PRET LIST").ConfigureAwait(false);
        if (site.UseXdupe) await Run($"SITE XDUPE {(site.XdupeMode == 0 ? 3 : site.XdupeMode)}").ConfigureAwait(false);
        if (site.UseSscn || site.SscnSupported) await Run("SSCN ON").ConfigureAwait(false);
        return probe;
    }

    public Task<RawCommandResult> SendRawCommandAsync(string siteName, string command, CancellationToken ct = default) =>
        SendRawCommandAsync(siteName, command, "", ct);

    public async Task<RawCommandResult> SendRawCommandAsync(string siteName, string command, string logAlias, CancellationToken ct = default)
    {
        siteName = (siteName ?? "").Trim();
        command = (command ?? "").Trim();
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentException("site is required");
        if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("command is required");
        if (command.Contains('\r') || command.Contains('\n')) throw new ArgumentException("command must be a single line");
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site, logAlias), ct).ConfigureAwait(false);
        var (code, msg) = await client.CommandAsync(command).ConfigureAwait(false);
        var result = new RawCommandResult
        {
            Site = siteName,
            Command = command,
            Code = code,
            Message = msg.Trim(),
            Ok = code / 100 is 1 or 2 or 3,
            ExecutedAt = DateTime.UtcNow,
        };
        Log("system", siteName, result.Ok ? "info" : "warn", $"raw command {command}: {code}");
        return result;
    }

    public async Task<DupeResult> CheckDupeAsync(string siteName, string path, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("dupe name is required");
        var entries = await ListRemoteAsync(siteName, path, ct).ConfigureAwait(false);
        var target = name.Trim().ToLowerInvariant();
        var result = new DupeResult { Site = siteName, Path = path, Name = name, CheckedAt = DateTime.UtcNow };
        foreach (var entry in entries)
        {
            var entryName = entry.Name.ToLowerInvariant();
            if (entryName == target || entryName.Contains(target) || target.Contains(entryName))
            {
                result.Exists = true;
                result.Matches.Add(entry);
            }
        }
        _store.AddDupe(result);
        return result;
    }

    public async Task<ReleaseCheck> CheckReleaseAsync(string siteName, string path, CancellationToken ct = default)
    {
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site), ct).ConfigureAwait(false);
        return await CheckReleaseOnAsync(client, site, path, persist: true, ct).ConfigureAwait(false);
    }

    // Same check over an ALREADY-OPEN connection (race completion probes borrow a
    // pooled conn instead of paying dial+TLS+login per probe). persist=false keeps
    // high-frequency probes out of the stored release history.
    private async Task<ReleaseCheck> CheckReleaseOnAsync(FtpClient client, Site site, string path, bool persist, CancellationToken ct)
    {
        var siteName = site.Name;
        var entries = await client.ListAsync(path, ct).ConfigureAwait(false);

        // A file only counts as PRESENT when it has bytes: glftpd shows mid-upload
        // files (growing) and 0-byte allocations, which must not complete a release.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
            if (e.Type is not ("dir" or "link") && e.Size > 0 && !FxpTransfer.IsIncompleteMarker(e.Name))
                seen.Add(e.Name);
        var markers = CompleteMarkersFor(site);

        var check = new ReleaseCheck
        {
            Site = siteName,
            Path = path,
            Name = RemoteBase(path),
            State = ReleaseState.Unknown,
            Files = entries,
            CheckedAt = DateTime.UtcNow,
        };

        var sfvVisible = false;
        foreach (var entry in entries)
        {
            var lower = entry.Name.ToLowerInvariant();
            foreach (var marker in markers)
            {
                if (CompletionMarkerMatches(entry.Name, marker))
                {
                    check.Markers.Add(entry.Name);
                    break;
                }
            }
            if (lower.EndsWith(".sfv") && entry.Type != "dir")
            {
                sfvVisible = true;
                string raw;
                try
                {
                    raw = await client.RetrieveTextAsync(FtpClient.JoinRemote(path, entry.Name), 1024 * 1024, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    check.Description = "SFV was visible but could not be read: " + ex.Message;
                    continue;
                }
                var parsed = Sfv.Parse(raw);
                if (parsed.Count == 0)
                {
                    if (check.Description.Length == 0) check.Description = "SFV was visible but contained no readable entries yet";
                    continue;
                }
                foreach (var file in parsed)
                {
                    file.Seen = seen.Contains(file.Name);
                    if (!file.Seen) check.Missing.Add(file.Name);
                    check.Sfv.Add(file);
                }
            }
        }

        if (check.Sfv.Count > 0)
        {
            if (check.Missing.Count == 0)
            {
                check.State = ReleaseState.Complete;
                if (check.Description.Length == 0) check.Description = "all files listed in SFV are visible";
            }
            else
            {
                check.State = ReleaseState.Incomplete;
                if (check.Description.Length == 0) check.Description = "some files listed in SFV are missing";
            }
        }
        else if (check.Markers.Count > 0)
        {
            if (sfvVisible)
            {
                check.State = ReleaseState.Unknown;
                if (check.Description.Length == 0) check.Description = "completion marker visible but SFV is not readable yet";
            }
            else
            {
                check.State = ReleaseState.Complete;
                check.Description = "completion marker visible";
            }
        }
        else
        {
            check.Description = "no completion marker or readable SFV found";
        }

        if (persist)
        {
            _store.UpsertRelease(check);
            Log("system", siteName, "info", $"release check {path}: {check.State} ({check.Description})");
        }
        return check;
    }

    // ---- FXP jobs ---------------------------------------------------------------------

    public Job StartFxp(TransferRequest req)
    {
        var job = CreateTransferJob(req);
        var run = RegisterJobToken(job.Id);
        _ = ScheduleTransfer(job.Id, req, run);
        return job;
    }

    public SpreadResult StartSpread(SpreadRequest req)
    {
        var result = CreateSpread(req);
        _ = req.Race ? Task.Run(() => RunSpreadAsync(result.Jobs, result.MaxParallel))
            : RunSpreadAsync(result.Jobs, result.MaxParallel);
        return result;
    }

    private Job CreateTransferJob(TransferRequest req)
    {
        req.FromSite = req.FromSite.Trim();
        req.ToSite = req.ToSite.Trim();
        req.SourcePath = req.SourcePath.Trim();
        req.DestPath = req.DestPath.Trim();
        req.Validate();
        var fromSite = _store.Site(req.FromSite) ?? throw new IOException($"from_site \"{req.FromSite}\": not found");
        var isMeshRace = req.Race && req.MeshSites.Count > 1;
        Site? toSite = null;
        if (!isMeshRace)
        {
            toSite = _store.Site(req.ToSite) ?? throw new IOException($"to_site \"{req.ToSite}\": not found");
            if (fromSite.BlockTransferFrom) throw new IOException($"site \"{req.FromSite}\" blocks transfers FROM it");
            if (toSite.BlockTransferTo) throw new IOException($"site \"{req.ToSite}\" blocks transfers TO it");
        }
        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = NewJobId(now),
            BatchId = req.BatchId,
            Type = req.Race ? JobType.Race : JobType.Fxp,
            State = JobState.Queued,
            Request = req,
            CreatedAt = now,
            Events = { new JobEvent { Time = now, Level = "info", Message = "job queued" } },
        };
        var saved = _store.UpsertJob(job);
        Log("transfer", TransferRoute(req), "info",
            $"queued {job.Type.ToString().ToLowerInvariant()} {req.SourcePath} -> {req.DestPath}");
        return saved;
    }

    // Is a src -> dst FXP allowed? Mirrors cbftp's spread gate: the blunt per-site
    // blocks (allowupload/allowdownload = NO) first, then the policy+exception model
    // from BOTH sides (source's target policy and destination's source policy).
    public bool TransferAllowed(Site src, Site dst)
    {
        if (src.BlockTransferFrom || dst.BlockTransferTo) return false;
        if (!src.IsAllowedTargetSite(dst.Name)) return false;
        if (!dst.IsAllowedSourceSite(src.Name)) return false;
        return true;
    }

    private SpreadResult CreateSpread(SpreadRequest req)
    {
        req.FromSite = req.FromSite.Trim();
        req.SourcePath = req.SourcePath.Trim();
        req.DestPath = req.DestPath.Trim();
        if (string.IsNullOrEmpty(req.DestPath)) req.DestPath = req.SourcePath;
        if (string.IsNullOrWhiteSpace(req.FromSite)) throw new ArgumentException("from_site is required");
        if (req.ToSites.Count == 0) throw new ArgumentException("to_sites is required");
        if (string.IsNullOrWhiteSpace(req.SourcePath)) throw new ArgumentException("source_path is required");
        if (_store.Site(req.FromSite) is null) throw new IOException($"from_site \"{req.FromSite}\": not found");

        var batchId = NewBatchId(DateTime.UtcNow);
        var jobs = new List<Job>();
        var label0 = string.IsNullOrEmpty(req.Label) ? batchId : req.Label;
        string SitePath(string site, string fallback) =>
            req.SitePaths.TryGetValue(site, out var path) && !string.IsNullOrWhiteSpace(path) ? path.Trim() : fallback;

        if (req.Race)
        {
            var names = new List<string> { req.FromSite };
            foreach (var raw in req.ToSites)
            {
                var t = raw.Trim();
                if (t.Length > 0 && !names.Any(n => n.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    names.Add(t);
            }
            var sites = names.Select(n => _store.Site(n) ?? throw new IOException($"site \"{n}\": not found")).ToList();
            var hasRoute = false;
            foreach (var src in sites)
                foreach (var dst in sites)
                    if (!src.Name.Equals(dst.Name, StringComparison.OrdinalIgnoreCase) && TransferAllowed(src, dst))
                        hasRoute = true;
            if (!hasRoute) throw new IOException("spread has no eligible site routes (check block transfer to/from on the sites)");

            // A race is one shared scoreboard regardless of site count. Every listed
            // site may supply a missing file to every allowed destination, so a
            // two-site race is bidirectional too instead of being pinned to FromSite.
            jobs.Add(CreateTransferJob(new TransferRequest
            {
                BatchId = batchId,
                FromSite = req.FromSite,
                ToSite = "mesh",
                SourcePath = SitePath(req.FromSite, req.SourcePath),
                DestPath = SitePath(names.First(n => !n.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase)), req.DestPath),
                MeshSites = names,
                SitePaths = new Dictionary<string, string>(req.SitePaths, StringComparer.OrdinalIgnoreCase),
                Race = true,
                DryRun = req.DryRun,
                ViaApi = req.ViaApi,
                Label = label0,
            }));
        }
        else
        {
            foreach (var raw in req.ToSites)
            {
                var target = raw.Trim();
                if (target.Length == 0 || target.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase)) continue;
                if (_store.Site(target) is null) throw new IOException($"to_site \"{target}\": not found");
                jobs.Add(CreateTransferJob(new TransferRequest
                {
                    BatchId = batchId,
                    FromSite = req.FromSite,
                    ToSite = target,
                    SourcePath = SitePath(req.FromSite, req.SourcePath),
                    DestPath = SitePath(target, req.DestPath),
                    Race = req.Race,
                    DryRun = req.DryRun,
                    ViaApi = req.ViaApi,
                    Label = label0,
                }));
            }
        }
        if (jobs.Count == 0) throw new IOException("spread has no eligible target sites (check block transfer to/from on the sites)");
        return new SpreadResult { BatchId = batchId, MaxParallel = req.Race && jobs.Count == 1 ? 1 : EffectiveSpreadParallel(req), Jobs = jobs };
    }

    private int EffectiveSpreadParallel(SpreadRequest req)
    {
        var settings = _store.Settings();
        var limit = req.Race ? settings.MaxConcurrentRaceJobs : settings.MaxConcurrentFxpJobs;
        if (req.MaxParallel > 0 && req.MaxParallel < limit) limit = req.MaxParallel;
        var source = _store.Site(req.FromSite);
        if (source is not null && source.DownloadSlots > 0 && source.DownloadSlots < limit)
            limit = source.DownloadSlots;
        return limit < 1 ? 1 : limit;
    }

    private async Task RunSpreadAsync(List<Job> jobs, int maxParallel)
    {
        if (maxParallel < 1) maxParallel = 1;
        if (jobs.All(job => !job.Request.Race))
        {
            var batch = new ManualTransferQueue.Limit("batch:" + jobs[0].BatchId, maxParallel);
            await Task.WhenAll(jobs.Select(job => ScheduleTransfer(job.Id, job.Request, RegisterJobToken(job.Id), batch))).ConfigureAwait(false);
            return;
        }
        using var sem = new SemaphoreSlim(maxParallel);
        using var raceStop = new CancellationTokenSource();
        var tasks = jobs.Select(async job =>
        {
            var entered = false;
            try
            {
                await sem.WaitAsync(raceStop.Token).ConfigureAwait(false);
                entered = true;
                if (raceStop.IsCancellationRequested)
                {
                    CancelJobInternal(job.Id, "race batch stopped after completion marker");
                    return;
                }

                var run = RegisterJobToken(job.Id);
                using var jobLinked = CancellationTokenSource.CreateLinkedTokenSource(raceStop.Token, run.Token);
                ArmJobWatchdog(job.Id, run);
                var stopRace = await RunTransferJobAsync(job.Id, job.Request, jobLinked.Token, run).ConfigureAwait(false);
                if (stopRace && job.Request.Race && !raceStop.IsCancellationRequested)
                {
                    Log("transfer", job.BatchId, "info", "race batch stopped after completion marker");
                    raceStop.Cancel();
                }
            }
            catch (OperationCanceledException) when (raceStop.IsCancellationRequested)
            {
                CancelJobInternal(job.Id, "race batch stopped after completion marker");
            }
            finally
            {
                if (entered) sem.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<bool> RunTransferJobAsync(string id, TransferRequest req, CancellationToken ct, JobRunControl run)
    {
        LogJob(id, "info", "job started");
        _store.UpdateJob(id, j => { j.State = JobState.Running; j.StartedAt = DateTime.UtcNow; });
        NotifyChanged();

        if (req.DryRun)
        {
            LogJob(id, "info", "dry run completed without connecting to FTP sites");
            FinishJob(id, null, run);
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            // NOTE: no "is the destination already complete?" probe here. On an announce
            // race those round trips are pure delay at the most latency-critical moment
            // The race loop checks completion once
            // it goes idle instead, and dupes are skipped per file via X-DUPE anyway.

            if (req.Race)
            {
                // The global scoreboard exists to arbitrate 3+ sites sharing pools. For a
                // straight server-to-server race it only adds scheduling latency: the old
                // directional loop borrows the shared pools directly and starts files with
                // no cross-race coordination, which is what wins the announce. Keep the
                // mesh+scoreboard for 3+ sites; take the lean path for one source→one dest.
                if (req.MeshSites.Count > 2)
                {
                    var meshComplete = await RunMeshRaceLoopAsync(id, req, ct).ConfigureAwait(false);
                    FinishJob(id, meshComplete ? null : new IOException("mesh race stopped idle before completion"), run);
                    return meshComplete;
                }

                // 2-site job comes in as a mesh request (ToSite = "mesh"); resolve the real
                // destination out of MeshSites so the directional loop and its config labels
                // point at the right box.
                if (req.MeshSites.Count == 2 && (string.IsNullOrWhiteSpace(req.ToSite) || req.ToSite.Equals("mesh", StringComparison.OrdinalIgnoreCase)))
                {
                    var dstName = req.MeshSites.FirstOrDefault(n => !n.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrWhiteSpace(dstName))
                    {
                        req.ToSite = dstName;
                        if (req.SitePaths.TryGetValue(dstName, out var dstPath) && !string.IsNullOrWhiteSpace(dstPath))
                            req.DestPath = dstPath;
                    }
                }

                var raceSrc = _store.Site(req.FromSite) ?? throw new IOException($"from_site \"{req.FromSite}\": not found");
                var raceDst = _store.Site(req.ToSite) ?? throw new IOException($"to_site \"{req.ToSite}\": not found");
                // Real racer: keep re-listing the source and moving new files as they land,
                // best-scored first, until the release is complete or the source goes idle.
                var raceResult = await RunRaceLoopAsync(id, req, raceSrc, raceDst, ct).ConfigureAwait(false);
                FinishJob(id, raceResult.Complete ? null : new IOException(raceResult.Reason), run);
                return raceResult.Complete;
            }

            var fxpSrc = _store.Site(req.FromSite) ?? throw new IOException($"from_site \"{req.FromSite}\": not found");
            var fxpDst = _store.Site(req.ToSite) ?? throw new IOException($"to_site \"{req.ToSite}\": not found");
            await FxpTransfer.TransferAsync(FtpConfig(fxpSrc, "", !req.ViaApi), FtpConfig(fxpDst, "", !req.ViaApi), req,
                (level, message) => LogJob(id, level, message), ct,
                onFilesFound: n =>
                {
                    if (n > 0) { _store.UpdateJobTransient(id, j => j.FilesTotal += n); NotifyChanged(); }
                },
                onFileDone: name =>
                {
                    _store.UpdateJobTransient(id, j => { j.FilesDone += 1; j.CurrentFile = name; });
                    NotifyChanged();
                }).ConfigureAwait(false);
            FinishJob(id, null, run);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelJobInternal(id, "race batch stopped after completion marker", run);
            UnregisterJobToken(id, run);
            return false;
        }
        catch (Exception ex)
        {
            if (!IsCurrentJobRun(id, run))
            {
                UnregisterJobToken(id, run);
                return false;
            }
            if (await ReleaseCompleteAfterTransferErrorAsync(id, req, ex, run).ConfigureAwait(false))
                return req.Race;

            FinishJob(id, ex, run);
            return false;
        }
    }

    // ---- race loop --------------------------------------------------------------------
    // Keep re-listing the source and moving newly-appeared files,
    // best-scored first (sfv/nfo first, then biggest), with per-file retry + backoff,
    // until the destination release is complete or the source stops producing files.

    private readonly struct RaceFile
    {
        public RaceFile(string abs, string rel, string name, string parentRel, long size)
        { Abs = abs; Rel = rel; Name = name; ParentRel = parentRel; Size = size; }
        public string Abs { get; }
        public string Rel { get; }
        public string Name { get; }
        public string ParentRel { get; }
        public long Size { get; }
    }

    private sealed class MeshSiteCtx
    {
        public required string Name { get; init; }
        public required Site Site { get; init; }
        public required string Path { get; init; }
        public required FtpClient.Config Config { get; init; }
        public required SitePool Pool { get; init; }
        public required List<string> Skiplist { get; init; }
        public Dictionary<string, RaceFile> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        // Files known to be present/complete on this site regardless of listed size —
        // set on a successful transfer here or when an X-DUPE refusal proves it exists.
        public HashSet<string> Confirmed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, MeshPendingDupe> PendingDupes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MadeDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim DirSem { get; } = new(1, 1);
        public bool Listed { get; set; }
        public bool CompletionSeen { get; set; }
    }

    private readonly struct MeshPick
    {
        public MeshPick(MeshSiteCtx src, MeshSiteCtx dst, RaceFile file)
        { Src = src; Dst = dst; File = file; }
        public MeshSiteCtx Src { get; }
        public MeshSiteCtx Dst { get; }
        public RaceFile File { get; }
    }

    private sealed class MeshPairReservation : IDisposable
    {
        public SitePool.TransferReservation Source { get; }
        public SitePool.TransferReservation Destination { get; }

        private MeshPairReservation(SitePool.TransferReservation source, SitePool.TransferReservation destination)
        { Source = source; Destination = destination; }

        public static MeshPairReservation? TryCreate(string owner, MeshPick pick)
        {
            var source = pick.Src.Pool.TryReserveTransferSlot(owner, true);
            if (source is null) return null;
            SitePool.TransferReservation? destination = null;
            try
            {
                destination = pick.Dst.Pool.TryReserveTransferSlot(owner, false);
                return destination is null ? null : new MeshPairReservation(source, destination);
            }
            finally
            {
                if (destination is null) source.Dispose();
            }
        }

        public void Dispose()
        {
            Source.Dispose();
            Destination.Dispose();
        }
    }

    private sealed class Attempt { public int Count; public long LastFailMs; }

    private async Task<bool> RunMeshRaceLoopAsync(string id, TransferRequest req, CancellationToken ct)
    {
        var settings = _store.Settings();
        var pollMs = settings.RacePollIntervalMs;
        var wakeMs = FastRaceWakeMs(pollMs);
        var maxIdle = settings.RaceMaxIdleCycles;
        var verbose = !req.ViaApi;
        var names = req.MeshSites
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count < 2) throw new IOException("mesh race needs at least two sites");

        string PathOn(string site)
        {
            if (req.SitePaths.TryGetValue(site, out var path) && !string.IsNullOrWhiteSpace(path)) return path;
            return site.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) ? req.SourcePath : req.DestPath;
        }

        var contexts = new List<MeshSiteCtx>();
        foreach (var name in names)
        {
            var site = _store.Site(name) ?? throw new IOException($"site \"{name}\": not found");
            var cfg = FtpConfig(site, name, verbose);
            contexts.Add(new MeshSiteCtx
            {
                Name = name,
                Site = site,
                Path = PathOn(name),
                Config = cfg,
                Pool = AcquirePool(name, site, cfg),
                Skiplist = MergePatternLists(settings.GlobalSkiplist, site.Skiplist),
            });
        }

        try
        {
            foreach (var c in contexts)
            {
                var slots = Math.Max(
                    ResolveSiteSlots(c.Site.DownloadSlots, c.Site),
                    ResolveSiteSlots(c.Site.UploadSlots, c.Site));
                _ = c.Pool.WarmUpAsync(Math.Min(c.Pool.Max, Math.Max(2, slots + 2)), ct)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            var started = DateTime.UtcNow;
            var sync = new object();
            var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempts = new Dictionary<string, Attempt>(StringComparer.OrdinalIgnoreCase);
            var attemptsLock = new object();
            var uploadBusyRetries = new ConcurrentDictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
            var uploadBusyNotBefore = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var attemptLogNotBefore = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var scheduledByDestination = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var scoreboard = new MeshScoreboard<MeshPick>();
            var dirtyFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var scoreboardChanged = true;
            var nextScoreboardRefresh = DateTime.MinValue;
            var knownSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var expectedFromSfv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var parsedSfvSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var sfvReads = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
            var nextSfvRead = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            using var stopWorkers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var workSignal = new SemaphoreSlim(0);
            var workerCount = MeshScheduling.WorkerCount(contexts.Select(c => (
                Math.Max(1, c.Pool.Max - 1),
                contexts.Any(dst => RouteAllowed(c, dst)) ? c.Pool.TransferCapacity(true) : 0,
                contexts.Any(src => RouteAllowed(src, c)) ? c.Pool.TransferCapacity(false) : 0)));
            using var scoreboardRegistration = _meshScoreboard.Register(id, workerCount, WakeWorkers);
            _meshScoreboard.SetPaused(id, IsJobPaused(id));
            using var unregisterScoreboard = stopWorkers.Token.Register(() =>
            {
                scoreboardRegistration.Dispose();
                foreach (var c in contexts) c.Pool.SetMeshDemand(id, false, false);
            });
            var raceDone = 0; // 0 running, 1 complete, 2 stopped idle
            var idleCycles = 0;
            DateTime? idleSince = null;
            var idleTimeout = RaceIdleTimeout(maxIdle, pollMs);
            var poll = 0;
            var sentCount = 0;
            long cumulative = 0;
            var recentTransfers = new List<(DateTime Start, DateTime End, long Size)>();
            var speedLock = new object();
            const double SpeedWindowSecs = 5.0;

            double CurrentSpeed(DateTime now)
            {
                var windowStart = now.AddSeconds(-SpeedWindowSecs);
                double bytes = 0;
                lock (speedLock)
                {
                    recentTransfers.RemoveAll(t => t.End < now.AddSeconds(-30));
                    foreach (var t in recentTransfers)
                    {
                        var s = t.Start > windowStart ? t.Start : windowStart;
                        var e = t.End < now ? t.End : now;
                        if (e <= s) continue;
                        var dur = (t.End - t.Start).TotalSeconds;
                        bytes += dur <= 0.05 ? t.Size : t.Size * ((e - s).TotalSeconds / dur);
                    }
                }
                return bytes / SpeedWindowSecs;
            }

            bool HasComplete(MeshSiteCtx site, string rel, long sourceSize)
            {
                if (site.PendingDupes.ContainsKey(rel)) return false;
                if (site.Confirmed.Contains(rel)) return true; // transferred here / proven by X-DUPE
                if (!site.Files.TryGetValue(rel, out var f)) return false;
                return f.Size > 0 && (sourceSize <= 0 || f.Size >= sourceSize);
            }

            async Task ReadAndParseSfvAsync(MeshSiteCtx ctx, RaceFile sfvFile, string readKey)
            {
                await Task.Yield();
                FtpClient? conn = null;
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(stopWorkers.Token);
                readCts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    if (sfvFile.Size < 8)
                    {
                        nextSfvRead[readKey] = DateTime.UtcNow.AddSeconds(2);
                        return;
                    }
                    conn = await ctx.Pool.TryBorrowAsync(readCts.Token).ConfigureAwait(false);
                    if (conn is null)
                    {
                        nextSfvRead[readKey] = DateTime.UtcNow.AddMilliseconds(500);
                        return;
                    }
                    var raw = await conn.RetrieveTextAsync(sfvFile.Abs, 1024 * 1024, readCts.Token).ConfigureAwait(false);
                    ctx.Pool.Return(conn);
                    conn = null;

                    var parsed = Sfv.Parse(raw);
                    if (parsed.Count == 0)
                    {
                        nextSfvRead[readKey] = DateTime.UtcNow.AddSeconds(1);
                        LogJobLive(id, "warn", $"could not parse {sfvFile.Rel} on {ctx.Name}: no readable SFV entries yet");
                        return;
                    }

                    var added = 0;
                    lock (sync)
                    {
                        foreach (var file in parsed)
                        {
                            var rel = string.IsNullOrEmpty(sfvFile.ParentRel) ? file.Name : sfvFile.ParentRel + "/" + file.Name;
                            if (expectedFromSfv.Add(rel)) added++;
                        }
                    }
                    parsedSfvSizes[sfvFile.Rel] = sfvFile.Size;
                    if (added > 0)
                    {
                        LogJobLive(id, "info", $"parsed {sfvFile.Rel}: {added} new expected file(s)");
                        workSignal.Release(Math.Min(added, 32));
                    }
                }
                catch (OperationCanceledException) when (stopWorkers.IsCancellationRequested)
                {
                    if (conn is not null) ctx.Pool.Drop(conn);
                }
                catch (Exception ex)
                {
                    if (conn is not null) ctx.Pool.Drop(conn);
                    nextSfvRead[readKey] = DateTime.UtcNow.AddSeconds(3);
                    LogJobLive(id, "warn", $"could not parse {sfvFile.Rel} on {ctx.Name}: {FirstLineOf(ex.Message)}");
                }
                finally { sfvReads.TryRemove(readKey, out _); }
            }

            void QueueSfvRead(MeshSiteCtx ctx, RaceFile sfvFile)
            {
                if (!sfvFile.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)) return;
                if (parsedSfvSizes.TryGetValue(sfvFile.Rel, out var parsedSize) && parsedSize >= sfvFile.Size) return;
                var key = sfvFile.Rel;
                if (nextSfvRead.TryGetValue(key, out var retryAt) && retryAt > DateTime.UtcNow) return;
                sfvReads.GetOrAdd(key, _ => ReadAndParseSfvAsync(ctx, sfvFile, key));
            }

            bool RouteAllowed(MeshSiteCtx src, MeshSiteCtx dst)
            {
                if (src.Name.Equals(dst.Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (!src.Site.AllowDownload || !dst.Site.AllowUpload) return false;
                return TransferAllowed(src.Site, dst.Site);
            }

            bool IsIngressBiasedDestination(MeshSiteCtx dst)
            {
                foreach (var peer in contexts)
                {
                    if (peer.Name.Equals(dst.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    var canReceiveFromPeer = peer.Site.AllowDownload && dst.Site.AllowUpload &&
                        TransferAllowed(peer.Site, dst.Site);
                    var canSendToPeer = dst.Site.AllowDownload && peer.Site.AllowUpload &&
                        TransferAllowed(dst.Site, peer.Site);
                    if (canReceiveFromPeer && !canSendToPeer) return true;
                }
                return false;
            }

            string RouteKey(MeshPick pick) => pick.Src.Name + "|" + pick.Dst.Name + "|" + pick.File.Rel;

            var allowedDestinations = contexts.ToDictionary(src => src,
                src => contexts.Where(dst => RouteAllowed(src, dst)).ToList());
            var ingressBias = contexts.ToDictionary(dst => dst, dst => IsIngressBiasedDestination(dst) ? 2_000_000L : 0L);

            void RefreshCandidates()
            {
                foreach (var rel in dirtyFiles)
                {
                    var candidates = new List<(MeshPick Pick, long Score)>();
                    var size = knownSizes.TryGetValue(rel, out var knownSize) ? knownSize : 0;
                    foreach (var src in contexts)
                    {
                        if (!src.Files.TryGetValue(rel, out var file) || IsUnreadableSfv(file) || !HasComplete(src, rel, size)) continue;
                        foreach (var dst in allowedDestinations[src])
                        {
                            if (!dst.Listed || HasComplete(dst, rel, size)) continue;
                            if (SkiplistMatches(FtpClient.JoinRemote(dst.Path, rel), file.Name, dst.Skiplist)) continue;
                            // Route speed can outweigh a small size difference, but never SFV/NFO priority.
                            var score = RaceScore(file.Name) * 1_000_000_000L +
                                Math.Min(999, Math.Max(0, file.Size / 1024 / 1024)) * 100_000L +
                                ingressBias[dst];
                            candidates.Add((new MeshPick(src, dst, file), score));
                        }
                    }
                    scoreboard.Replace(rel, candidates);
                }
                dirtyFiles.Clear();
            }

            bool CoveredForReachableMesh((string Rel, long Size) file)
            {
                foreach (var dst in contexts)
                {
                    if (SkiplistMatches(FtpClient.JoinRemote(dst.Path, file.Rel), RemoteBase(file.Rel), dst.Skiplist)) continue;
                    if (HasComplete(dst, file.Rel, file.Size)) continue;
                    if (contexts.Any(src => RouteAllowed(src, dst))) return false;
                }
                return true;
            }

            bool HasMeshWorkOutstanding(List<(string Rel, long Size)> known)
            {
                if (contexts.Any(c => c.PendingDupes.Count > 0)) return true;
                var now = DateTime.UtcNow;
                var nowMs = (now - started).TotalMilliseconds;
                foreach (var f in known)
                {
                    foreach (var src in contexts)
                    {
                        if (!HasComplete(src, f.Rel, f.Size)) continue;
                        foreach (var dst in contexts)
                        {
                            if (!dst.Listed) return true;
                            if (!RouteAllowed(src, dst)) continue;
                            if (SkiplistMatches(FtpClient.JoinRemote(dst.Path, f.Rel), RemoteBase(f.Rel), dst.Skiplist)) continue;
                            if (HasComplete(dst, f.Rel, f.Size)) continue;
                            var key = f.Rel + "|" + dst.Name;
                            if (inFlight.Contains(key) || dst.PendingDupes.ContainsKey(f.Rel)) return true;
                            var routeKey = src.Name + "|" + dst.Name + "|" + f.Rel;
                            lock (attemptsLock)
                            {
                                if (AttemptsExceeded(attempts, routeKey)) continue;
                                if (InBackoff(attempts, routeKey, nowMs)) return true;
                            }
                            if (uploadBusyNotBefore.TryGetValue(routeKey, out var busyUntil) && busyUntil > now)
                                return true;
                            return true;
                        }
                    }
                }
                return false;
            }

            GlobalMeshScoreboard<MeshPick>.Claim? TakeBest()
            {
                lock (sync)
                {
                    var now = DateTime.UtcNow;
                    var nowMs = (now - started).TotalMilliseconds;
                    foreach (var site in contexts)
                        foreach (var rel in site.PendingDupes.Where(x => x.Value.RetryAt <= now).Select(x => x.Key).ToList())
                        {
                            site.PendingDupes.Remove(rel);
                            site.Files.Remove(rel); // retry an abandoned upload; never trust its old partial listing
                            site.Confirmed.Remove(rel);
                            dirtyFiles.Add(rel);
                        }
                    if (scoreboardChanged || dirtyFiles.Count > 0 || now >= nextScoreboardRefresh)
                    {
                        RefreshCandidates();
                        var sourceDemand = new HashSet<MeshSiteCtx>();
                        var destinationDemand = new HashSet<MeshSiteCtx>();
                        var runnable = new List<GlobalMeshScoreboard<MeshPick>.Candidate>();
                        var routeScores = new Dictionary<(MeshSiteCtx, MeshSiteCtx), long>();
                        foreach (var candidate in scoreboard.Ordered)
                        {
                            var pick = candidate.Pick;
                            var key = pick.File.Rel + "|" + pick.Dst.Name;
                            var routeKey = RouteKey(pick);
                            if (inFlight.Contains(key) || pick.Dst.PendingDupes.ContainsKey(pick.File.Rel)) continue;
                            if (uploadBusyNotBefore.TryGetValue(routeKey, out var busyUntil) && busyUntil > now) continue;
                            var failCount = 0;
                            lock (attemptsLock)
                            {
                                if (InBackoff(attempts, routeKey, nowMs) || AttemptsExceeded(attempts, routeKey)) continue;
                                if (attempts.TryGetValue(routeKey, out var att)) failCount = att.Count;
                            }
                            if (uploadBusyRetries.TryGetValue(routeKey, out var busyRetry)) failCount += busyRetry.Count;
                            sourceDemand.Add(pick.Src);
                            destinationDemand.Add(pick.Dst);
                            var route = (pick.Src, pick.Dst);
                            if (!routeScores.TryGetValue(route, out var routeScore))
                            {
                                routeScore = Math.Min(250_000_000L, RoutePerformanceScore(pick.Src.Name, pick.Dst.Name) * 250);
                                routeScores[route] = routeScore;
                            }
                            scheduledByDestination.TryGetValue(pick.Dst.Name, out var scheduled);
                            var score = candidate.Score + routeScore - Math.Min(250_000_000L, scheduled * 3_000_000L) -
                                Math.Min(failCount, 3) * 6_000_000_000L;
                            runnable.Add(new GlobalMeshScoreboard<MeshPick>.Candidate(pick, score,
                                pick.Dst.Name + "|" + FtpClient.JoinRemote(pick.Dst.Path, pick.File.Rel),
                                pick.Src.Pool, pick.Dst.Pool,
                                () => pick.Src.Pool.CanBorrowTransfer(id, true) && pick.Dst.Pool.CanBorrowTransfer(id, false),
                                () => MeshPairReservation.TryCreate(id, pick),
                                () => pick.Src.Pool.FreeTransferSlots(true),
                                () => pick.Dst.Pool.FreeTransferSlots(false)));
                        }
                        foreach (var site in contexts)
                            site.Pool.SetMeshDemand(id, !IsJobPaused(id) && sourceDemand.Contains(site), !IsJobPaused(id) && destinationDemand.Contains(site));
                        _meshScoreboard.Publish(scoreboardRegistration, runnable);
                        scoreboardChanged = false;
                        nextScoreboardRefresh = now.AddMilliseconds(100);
                    }
                    var claim = _meshScoreboard.TryTake(scoreboardRegistration);
                    if (claim is not null)
                    {
                        var best = claim.Candidate.Value;
                        inFlight.Add(best.File.Rel + "|" + best.Dst.Name);
                        scheduledByDestination.TryGetValue(best.Dst.Name, out var scheduled);
                        scheduledByDestination[best.Dst.Name] = scheduled + 1;
                        scoreboardChanged = true;
                    }
                    return claim;
                }
            }

            void FinishPick(MeshPick pick, bool requeue)
            {
                lock (sync)
                {
                    inFlight.Remove(pick.File.Rel + "|" + pick.Dst.Name);
                    scoreboardChanged = true;
                }
                if (requeue) workSignal.Release();
            }

            void RecordSuccess(MeshPick pick, DateTime startedAt)
            {
                lock (sync)
                {
                    // Now present on dst with the real size, so it can feed onward to a
                    // third site, and is confirmed complete.
                    pick.Dst.Files[pick.File.Rel] = new RaceFile(
                        FtpClient.JoinRemote(pick.Dst.Path, pick.File.Rel),
                        pick.File.Rel, pick.File.Name, pick.File.ParentRel, pick.File.Size);
                    pick.Dst.Confirmed.Add(pick.File.Rel);
                    pick.Dst.PendingDupes.Remove(pick.File.Rel);
                    dirtyFiles.Add(pick.File.Rel);
                }
                Interlocked.Add(ref cumulative, Math.Max(0, pick.File.Size));
                var cum = Interlocked.Read(ref cumulative);
                var now = DateTime.UtcNow;
                lock (speedLock) recentTransfers.Add((startedAt, now, Math.Max(0, pick.File.Size)));
                var routeKey = RouteKey(pick);
                uploadBusyRetries.TryRemove(routeKey, out _);
                uploadBusyNotBefore.TryRemove(routeKey, out _);
                attemptLogNotBefore.TryRemove(pick.File.Rel + "|" + pick.Dst.Name, out _);
                var speed = CurrentSpeed(now);
                var sent = Interlocked.Increment(ref sentCount);
                _store.UpdateJobTransient(id, j =>
                {
                    j.FilesDone = sent;
                    j.BytesDone = cum;
                    j.CumulativeBytes = cum;
                    j.SpeedBps = speed;
                    j.CurrentFile = pick.File.Name;
                });
                workSignal.Release(Math.Max(1, contexts.Count - 2));
                NotifyChangedThrottled();
            }

            async Task ListerAsync(MeshSiteCtx ctx)
            {
                var listFails = 0;
                while (!ct.IsCancellationRequested && Volatile.Read(ref raceDone) == 0)
                {
                    var added = 0;
                    FtpClient? conn = null;
                    try
                    {
                        conn = await ctx.Pool.BorrowAsync(ct).ConfigureAwait(false);
                        var listingStarted = DateTime.UtcNow;
                        var listTimer = System.Diagnostics.Stopwatch.StartNew();
                        var sawCompletion = false;
                        var files = await ListSourceFilesAsync(conn, ctx.Path, ctx.Skiplist, ct,
                            completeMarkers: CompleteMarkersFor(ctx.Site),
                            onCompletionMarker: _ => sawCompletion = true, throwOnRootFailure: true).ConfigureAwait(false);
                        ctx.Pool.Return(conn);
                        conn = null;
                        listFails = 0;
                        lock (sync)
                        {
                            if (!ctx.Listed) dirtyFiles.UnionWith(knownSizes.Keys);
                            ctx.Listed = true;
                            if (sawCompletion) ctx.CompletionSeen = true;
                            foreach (var f in files)
                            {
                                knownSizes.AddOrUpdate(f.Rel, Math.Max(0, f.Size), (_, old) => Math.Max(old, Math.Max(0, f.Size)));
                                if (!ctx.Files.TryGetValue(f.Rel, out var old) || old.Size != f.Size)
                                {
                                    ctx.Files[f.Rel] = f;
                                    dirtyFiles.Add(f.Rel);
                                    added++;
                                }
                                if (ctx.PendingDupes.TryGetValue(f.Rel, out var pending) &&
                                    pending.IsConfirmedByListing(listingStarted, f.Size, knownSizes[f.Rel]))
                                {
                                    ctx.PendingDupes.Remove(f.Rel);
                                    dirtyFiles.Add(f.Rel);
                                    added++;
                                }
                            }
                            // Register the parse task before exposing this listing to the
                            // coordinator. Otherwise a completion marker can end the race
                            // in the tiny window between listing the SFV and parsing it.
                            foreach (var sfv in files.Where(f => f.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase)))
                                QueueSfvRead(ctx, sfv);
                        }
                        var remainingMs = pollMs - (int)listTimer.ElapsedMilliseconds;
                        if (added > 0) workSignal.Release(Math.Min(added, 32));
                        if (remainingMs > 0) await Task.Delay(remainingMs, ct).ConfigureAwait(false);
                        continue;
                    }
                    catch (OperationCanceledException) { if (conn is not null) ctx.Pool.Drop(conn); return; }
                    catch (Exception ex)
                    {
                        if (conn is not null) ctx.Pool.Drop(conn);
                        listFails++;
                        if (listFails == 1 || listFails % 15 == 0)
                            LogJobLive(id, "warn", $"{ctx.Name} list failed ({listFails}x): {FirstLineOf(ex.Message)}");
                    }

                    if (added > 0) workSignal.Release(Math.Min(added, 32));
                    try { await Task.Delay(pollMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            async Task CoordinatorAsync()
            {
                var logEvery = Math.Max(20, 5000 / Math.Max(1, pollMs));
                while (!ct.IsCancellationRequested && Volatile.Read(ref raceDone) == 0)
                {
                    if (IsJobPaused(id))
                        foreach (var site in contexts) site.Pool.SetMeshDemand(id, false, false);
                    poll++;
                    int knownFiles, coveredFiles, inFlightCount, expectedFiles;
                    long knownBytes;
                    bool allListed, completionMarkerSeen, completionMarkerCanComplete, sfvComplete, sfvReadPending, workOutstanding;
                    lock (sync)
                    {
                        var known = contexts
                            .SelectMany(c => c.Files.Values)
                            .GroupBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                            .Select(g => (Rel: g.Key, Size: g.Max(f => f.Size)))
                            .ToList();
                        foreach (var rel in expectedFromSfv)
                            if (!known.Any(f => f.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)))
                                known.Add((rel, 0));
                        knownFiles = known.Count;
                        knownBytes = known.Sum(f => Math.Max(0, f.Size));
                        coveredFiles = known.Count(CoveredForReachableMesh);
                        inFlightCount = inFlight.Count;
                        expectedFiles = expectedFromSfv.Count;
                        allListed = contexts.All(c => c.Listed);
                        completionMarkerSeen = contexts.Any(c => c.CompletionSeen);
                        var sfvVisibleInMesh = known.Any(f => f.Rel.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && f.Size > 0);
                        completionMarkerCanComplete = completionMarkerSeen && !sfvVisibleInMesh;
                        sfvReadPending = !sfvReads.IsEmpty;
                        workOutstanding = HasMeshWorkOutstanding(known);
                        sfvComplete = expectedFiles > 0 && expectedFromSfv.All(rel =>
                            CoveredForReachableMesh((rel, knownSizes.TryGetValue(rel, out var size) ? size : 0)));
                    }
                    _store.UpdateJobTransient(id, j =>
                    {
                        j.FilesTotal = Math.Max(j.FilesTotal, knownFiles);
                        j.FilesCovered = coveredFiles;
                        j.BytesTotal = Math.Max(j.BytesTotal, knownBytes);
                        j.SpeedBps = CurrentSpeed(DateTime.UtcNow);
                    });
                    if (poll % logEvery == 1)
                        LogJobLive(id, "info", $"mesh poll #{poll}: {coveredFiles}/{knownFiles} covered, {inFlightCount} in flight, {sentCount} raced");
                    if (allListed && !sfvReadPending && inFlightCount == 0 && knownFiles > 0 && coveredFiles == knownFiles &&
                        (sfvComplete || completionMarkerCanComplete))
                    {
                        var reason = sfvComplete
                            ? $"all {expectedFiles} SFV file(s) are covered on every site"
                            : "completion marker seen and all listed files are covered on every site";
                        LogJob(id, "info", $"mesh race complete: {reason}");
                        Volatile.Write(ref raceDone, 1);
                        stopWorkers.Cancel();
                        return;
                    }
                    if (inFlightCount == 0 && !workOutstanding)
                    {
                        idleCycles++;
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince.Value >= idleTimeout)
                        {
                            LogJob(id, "info", $"mesh race stopped after {(DateTime.UtcNow - idleSince.Value).TotalSeconds:0.0}s idle with no work");
                            Volatile.Write(ref raceDone, 2);
                            stopWorkers.Cancel();
                            return;
                        }
                    }
                    else
                    {
                        idleCycles = 0;
                        idleSince = null;
                    }
                    try { await Task.Delay(pollMs, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            async Task WorkerAsync(int workerNo)
            {
                var wct = stopWorkers.Token;
                while (!wct.IsCancellationRequested)
                {
                    try { await WaitWhilePausedAsync(id, wct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    using var dispatch = TakeBest();
                    if (dispatch is null)
                    {
                        if (Volatile.Read(ref raceDone) != 0) return;
                        try { await workSignal.WaitAsync(wakeMs, wct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        continue;
                    }

                    MeshPick? pick = dispatch.Candidate.Value;
                    var pair = (MeshPairReservation)dispatch.Reservation;

                    FtpClient? s = null, d = null;
                    var srcOk = true; var dstOk = true; var requeue = false; var cancelled = false;
                    var slowSkipped = false;
                    var xferStart = DateTime.UtcNow;
                    try
                    {
                        s = await pair.Source.OpenAsync(wct).ConfigureAwait(false);
                        d = await pair.Destination.OpenAsync(wct).ConfigureAwait(false);
                        wct.ThrowIfCancellationRequested();

                        await pick.Value.Dst.DirSem.WaitAsync(ct).ConfigureAwait(false);
                        try { await EnsureDestDirAsync(d, pick.Value.Dst.Path, pick.Value.File.ParentRel, pick.Value.Dst.MadeDirs, id).ConfigureAwait(false); }
                        finally { pick.Value.Dst.DirSem.Release(); }

                        var absDst = FtpClient.JoinRemote(pick.Value.Dst.Path, pick.Value.File.Rel);
                        var attemptKey = pick.Value.File.Rel + "|" + pick.Value.Dst.Name;
                        var attemptNow = DateTime.UtcNow;
                        if (!attemptLogNotBefore.TryGetValue(attemptKey, out var nextAttemptLog) || nextAttemptLog <= attemptNow)
                        {
                            LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: sending {pick.Value.File.Rel} ({HumanBytes(pick.Value.File.Size)})");
                            attemptLogNotBefore[attemptKey] = attemptNow.AddSeconds(2);
                        }
                        _store.UpdateJobTransient(id, j =>
                        {
                            j.CurrentFile = pick.Value.File.Name;
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "wait");
                            if (row is null)
                            {
                                row = new FileTransfer
                                {
                                    Name = pick.Value.File.Rel,
                                    FromSite = pick.Value.Src.Name,
                                    ToSite = pick.Value.Dst.Name,
                                };
                                j.Files.Add(row);
                            }
                            row.Size = Math.Max(0, pick.Value.File.Size);
                            row.StartedAt = xferStart;
                            row.Seconds = 0;
                            row.Bps = 0;
                            row.Status = "active";
                            row.Error = "";
                        });
                        var xfer = FxpTransfer.TransferSingleAsync(s, d, pick.Value.Dst.Config, pick.Value.File.Abs, absDst,
                            (level, message) => LogJobLive(id, level, message), ct);
                        // Stall-guard (same as the directional path): a dead data channel
                        // (TLS role deadlock, dropped conn) must never squat a worker + two
                        // connections until the job watchdog. Abort below the slow-skip
                        // threshold, or ~1 MB/s with a 45s floor when slow-skip is off.
                        if (pick.Value.File.Size > 0)
                        {
                            var slowKBps = Math.Max(pick.Value.Src.Site.SlowSkipKBps, pick.Value.Dst.Site.SlowSkipKBps);
                            var guardKBps = slowKBps > 0 ? slowKBps : 1024;
                            var floor = slowKBps > 0 ? 15 : 45;
                            var budget = TimeSpan.FromSeconds(Math.Max(floor, pick.Value.File.Size / 1024.0 / guardKBps + 10));
                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var finished = await Task.WhenAny(xfer, Task.Delay(budget, delayCts.Token)).ConfigureAwait(false);
                            if (finished == xfer) delayCts.Cancel();
                            else
                            {
                                slowSkipped = true;
                                try { await s.NudgeAbortAsync().ConfigureAwait(false); } catch { }
                                try { await d.NudgeAbortAsync().ConfigureAwait(false); } catch { }
                            }
                        }
                        await xfer.ConfigureAwait(false);
                        if (slowSkipped) { srcOk = false; dstOk = false; } // ABOR replies unread — drop conns

                        var dur = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null)
                            {
                                row.Status = "done";
                                row.Seconds = dur;
                                row.Bps = row.Size / dur;
                            }
                        });
                        RecordSuccess(pick.Value, xferStart);
                        LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: raced {pick.Value.File.Rel} in {dur:0.00}s");
                        RecordRoutePerformance(pick.Value.Src.Name, pick.Value.Dst.Name, pick.Value.File.Size / dur);
                        _store.AddSiteTraffic(pick.Value.Src.Name, pick.Value.File.Size, 0, dur);
                        _store.AddSiteTraffic(pick.Value.Dst.Name, 0, pick.Value.File.Size, dur);
                    }
                    catch (Exception ex) when (slowSkipped)
                    {
                        // We aborted it for stalling. Connections carry unread ABOR replies —
                        // drop them. Retry later; counts toward the give-up cap.
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var key = RouteKey(pick.Value);
                        var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) RecordFail(attempts, key, nowMs);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null) { row.Status = "slow"; row.Error = FirstLineOf(ex.Message); row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds); }
                        });
                        LogJobLive(id, "warn", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: aborted {pick.Value.File.Name}: stalled");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        cancelled = true; srcOk = false; dstOk = false;
                    }
                    catch (Exception ex) when (FxpTransfer.TryGetServerSlotLimit(ex, out var downloadLimit, out var serverLimit))
                    {
                        // The daemon knows the effective per-user limit better than the
                        // imported site settings. Learn it for the rest of this process;
                        // this is capacity pressure, not a failed file attempt.
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var limitedPool = downloadLimit ? pick.Value.Src.Pool : pick.Value.Dst.Pool;
                        var changed = limitedPool.LimitTransferSlots(downloadLimit, serverLimit);
                        var key = RouteKey(pick.Value);
                        uploadBusyNotBefore[key] = DateTime.UtcNow.AddMilliseconds(250);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null)
                            {
                                row.Status = "wait";
                                row.Error = $"server slot limit {serverLimit}; retrying";
                                row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                            }
                        });
                        if (changed)
                            LogJobLive(id, "warn", $"{(downloadLimit ? pick.Value.Src.Name + " download" : pick.Value.Dst.Name + " upload")} slots reduced to server limit {serverLimit}");
                    }
                    catch (Exception ex) when (FxpTransfer.IsDestinationBusyError(ex) || FxpTransfer.IsDestinationDupeError(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        var now = DateTime.UtcNow;
                        lock (sync)
                        {
                            // X-DUPE is evidence of occupancy, not of a finished upload.
                            var names = FxpTransfer.ParseXdupeNames(ex);
                            names.Add(pick.Value.File.Name);
                            foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
                            {
                                var rel = string.IsNullOrEmpty(pick.Value.File.ParentRel) ? name : pick.Value.File.ParentRel + "/" + name;
                                if (pick.Value.Dst.Confirmed.Contains(rel)) continue;
                                pick.Value.Dst.PendingDupes.TryAdd(rel, new MeshPendingDupe(now));
                                dirtyFiles.Add(rel);
                            }
                        }
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null) { row.Status = "dupe"; row.Error = "destination occupied; awaiting listing"; }
                        });
                        LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: skipped {pick.Value.File.Name}: destination occupied; checking completion via listing");
                    }
                    catch (Exception ex) when (FxpTransfer.IsBeingUploaded(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        requeue = true;
                        var key = RouteKey(pick.Value);
                        var delayMs = RegisterUploadBusy(uploadBusyRetries, key, pick.Value.File.Size);
                        uploadBusyNotBefore[key] = DateTime.UtcNow.AddMilliseconds(delayMs);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null)
                            {
                                row.Status = "wait";
                                row.Error = FirstLineOf(ex.Message);
                                row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var key = RouteKey(pick.Value);
                        var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) RecordFail(attempts, key, nowMs);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel &&
                                x.FromSite == pick.Value.Src.Name && x.ToSite == pick.Value.Dst.Name && x.Status == "active");
                            if (row is not null)
                            {
                                row.Status = "fail";
                                row.Error = FirstLineOf(ex.Message);
                                row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                            }
                        });
                        LogJobLive(id, "warn", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: transfer failed for {pick.Value.File.Name}: {FirstLineOf(ex.Message)}");
                    }
                    finally
                    {
                        if (s is not null) { if (srcOk) pick.Value.Src.Pool.ReturnTransfer(id, asSource: true, s); else pick.Value.Src.Pool.DropTransfer(id, asSource: true, s); }
                        if (d is not null) { if (dstOk) pick.Value.Dst.Pool.ReturnTransfer(id, asSource: false, d); else pick.Value.Dst.Pool.DropTransfer(id, asSource: false, d); }
                        FinishPick(pick.Value, requeue && !cancelled);
                    }
                }
            }

            var eligibleRoutes = contexts
                .SelectMany(src => contexts
                    .Where(dst => RouteAllowed(src, dst))
                    .Select(dst => $"{src.Name}>{dst.Name}"))
                .ToList();
            LogJob(id, "info", $"mesh race started with {contexts.Count} site(s), routes {string.Join(", ", eligibleRoutes)}, poll every {pollMs}ms, stop after ~{FormatDuration(idleTimeout)} idle");
            var listers = contexts.Select(ListerAsync).ToList();
            void WakeWorkers()
            {
                try { if (workSignal.CurrentCount < workerCount) workSignal.Release(); }
                catch (ObjectDisposedException) { } // a return callback may already have been dispatched at teardown
            }
            foreach (var c in contexts) c.Pool.AvailabilityChanged += WakeWorkers;
            var workers = Enumerable.Range(1, workerCount).Select(WorkerAsync).ToList();
            var coordinator = CoordinatorAsync();
            try { await Task.WhenAll(listers.Concat(workers).Append(coordinator)).ConfigureAwait(false); }
            finally
            {
                foreach (var c in contexts)
                {
                    c.Pool.AvailabilityChanged -= WakeWorkers;
                    c.Pool.SetMeshDemand(id, false, false);
                }
            }
            if (Volatile.Read(ref raceDone) == 1)
                return true;

            bool fullyCovered;
            int finalCovered;
            int finalTotal;
            lock (sync)
            {
                var known = contexts.SelectMany(c => c.Files.Values)
                    .GroupBy(f => f.Rel, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (Rel: g.Key, Size: g.Max(f => f.Size)))
                    .ToList();
                foreach (var rel in expectedFromSfv)
                    if (!known.Any(f => f.Rel.Equals(rel, StringComparison.OrdinalIgnoreCase)))
                        known.Add((rel, 0));
                finalTotal = known.Count;
                finalCovered = known.Count(CoveredForReachableMesh);
                fullyCovered = contexts.All(c => c.Listed) && finalTotal > 0 &&
                    known.All(CoveredForReachableMesh);
            }
            if (!fullyCovered)
                LogJob(id, "warn", $"mesh race incomplete: {finalCovered}/{finalTotal} file(s) covered on every site");
            return fullyCovered;
        }
        finally
        {
            foreach (var c in contexts) ReleasePool(c.Name);
        }
    }

    private async Task<(bool Complete, string Reason)> RunRaceLoopAsync(
        string id, TransferRequest req, Site srcSite, Site dstSite, CancellationToken ct)
    {
        var settings = _store.Settings();
        var pollMs = settings.RacePollIntervalMs;
        var wakeMs = FastRaceWakeMs(pollMs);
        var maxIdle = settings.RaceMaxIdleCycles;
        var destinationPrecheck = settings.RaceDestinationPrecheck;
        var skiplist = MergePatternLists(settings.GlobalSkiplist, srcSite.Skiplist);

        // API-triggered races run silent (protocol tracing costs throughput); a race
        // started by hand from the browser keeps its FTP Log.
        var verbose = !req.ViaApi;
        var srcCfg = FtpConfig(srcSite, req.FromSite, verbose);
        var dstCfg = FtpConfig(dstSite, req.ToSite, verbose);

        // Each site's connection slots are shared across ALL races:
        // per-site connection pools that every race borrows from. A race that finds no
        // new files this poll holds no connections, so its slots are free for another
        // race to grab. Each race drains its scored queue across up to `wantSlots`
        // parallel borrows, bounded by whatever the shared pool has free right now.
        var srcPool = AcquirePool(req.FromSite, srcSite, srcCfg);
        var dstPool = AcquirePool(req.ToSite, dstSite, dstCfg);
        try
        {
            // Transfer width: the sites' slot settings, held one below the source
            // pool's cap so the lister always has a connection and never queues
            // behind the transfer workers.
            var wantSlots = Math.Max(1, Math.Min(ResolveRaceSlots(srcSite, dstSite), Math.Min(srcPool.Max - 1, dstPool.Max - 1)));
            // Slow-skip threshold (KB/s): strictest of the two sites; 0 = off.
            var slowKBps = Math.Max(srcSite.SlowSkipKBps, dstSite.SlowSkipKBps);
            var transferred = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var destinationFiles = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var announced = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var attempts = new Dictionary<string, Attempt>(StringComparer.OrdinalIgnoreCase);
            var attemptsLock = new object();
            var madeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var dirSem = new SemaphoreSlim(1, 1);
            var started = DateTime.UtcNow;
            long cumulative = 0;
            var idleCycles = 0;
            DateTime? idleSince = null;
            var idleTimeout = RaceIdleTimeout(maxIdle, pollMs);
            var poll = 0;
            var lastFound = -1;
            var listFails = 0;
            var lastSourceListMs = 0;
            var lastDestListMs = 0;
            var noSrcSlot = 0;
            var noDstSlot = 0;
            var uploadBusy = 0;
            // In FXP the bytes never pass through us, so we can only account for a file
            // once it lands. Crediting its whole size at that instant makes the readout
            // spike and dip. Instead we remember each completed transfer's interval and
            // spread its bytes evenly across the time it actually took — the speed is
            // then the bytes attributable to the last few seconds, across all slots.
            var recentTransfers = new List<(DateTime Start, DateTime End, long Size)>();
            var speedLock = new object();
            const double SpeedWindowSecs = 5.0;

            double CurrentSpeed(DateTime now)
            {
                var windowStart = now.AddSeconds(-SpeedWindowSecs);
                double bytes = 0;
                lock (speedLock)
                {
                    recentTransfers.RemoveAll(t => t.End < now.AddSeconds(-30));
                    foreach (var t in recentTransfers)
                    {
                        var s = t.Start > windowStart ? t.Start : windowStart;
                        var e = t.End < now ? t.End : now;
                        if (e <= s) continue;
                        var dur = (t.End - t.Start).TotalSeconds;
                        if (dur <= 0.05) { bytes += t.Size; continue; }   // too quick to spread
                        bytes += t.Size * ((e - s).TotalSeconds / dur);
                    }
                }
                return bytes / SpeedWindowSecs;
            }

            LogJob(id, "info", $"race started (up to {wantSlots} shared slot(s), poll every {pollMs}ms, stop after ~{FormatDuration(idleTimeout)} idle)");

            // Warm missing connections CONCURRENTLY and in the BACKGROUND. Dialing + TLS
            // + login is ~1s per connection; the lister and workers must never wait for
            // this — they borrow whatever is already warm and extras fill in behind.
            _ = Task.WhenAll(
                    srcPool.WarmUpAsync(Math.Min(srcPool.Max, wantSlots + 4), ct),   // + lister & staging headroom
                    dstPool.WarmUpAsync(Math.Min(dstPool.Max, wantSlots + 3), ct))
                .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);

            // Continuous engine: a dedicated lister keeps polling the source
            // and feeding a live scored queue WHILE the workers transfer in parallel.
            // No list→drain→list barrier — a file that lands mid-transfer starts moving
            // the moment a slot frees up.
            var pending = new List<RaceFile>();
            var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expectedFromSfv = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sync = new object();
            var raceDone = 0; // 0 running, 1 complete, 2 stopped idle
            var sentCount = 0; // files WE actually moved (transferred also holds opponents' files)
            using var stopWorkers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Streams are capped at wantSlots; when the pools have login headroom we run
            // extra workers that pre-negotiate (PRET/PASV/PORT) the NEXT files and sit at
            // this gate, firing STOR/RETR the instant a data slot frees.
            using var dataGate = new SemaphoreSlim(Math.Max(1, wantSlots), Math.Max(1, wantSlots));
            var stagingHeadroom = Math.Clamp(Math.Min(srcPool.Max - 1, dstPool.Max - 1) - wantSlots, 0, 3);
            var workerCount = Math.Max(1, wantSlots + stagingHeadroom);
            // Wakes idle workers the instant the lister queues new files — no idle polling.
            using var workSignal = new SemaphoreSlim(0);
            // Files we must not retry before a given time (source still uploading them).
            var notBefore = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var uploadBusyRetries = new ConcurrentDictionary<string, (int Count, long Size)>(StringComparer.OrdinalIgnoreCase);
            // Source sizes per rel path (to judge whether a dest copy is COMPLETE) and
            // the set of files WE moved (never un-concede those).
            var sourceSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var sentByUs = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var parsedSfvSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var nextSfvRead = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            var sfvReads = new ConcurrentDictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
            var completeMarkers = CompleteMarkersFor(dstSite);
            var destinationComplete = 0;
            var completionDescription = "";
            var incompleteReason = "race stopped idle before completion";
            var nextCompletionProbe = DateTime.MinValue;
            var nextNestedSourceList = DateTime.MinValue;
            var nestedSourceFiles = new Dictionary<string, RaceFile>(StringComparer.OrdinalIgnoreCase);

            // NOTE: no separate dest-setup step. Every worker calls EnsureDestDirAsync for
            // its own file under dirSem, and MKD is idempotent, so the first file creates
            // the release root itself. A pre-step here cost the announce-critical moment a
            // pooled dst connection, mutated the shared madeDirs set outside dirSem, and
            // could abort an otherwise healthy race on a transient MKD failure.

            void SortPending()
            {
                pending.Sort((x, y) =>
                {
                    var c = RaceScore(y.Name).CompareTo(RaceScore(x.Name));
                    return c != 0 ? c : y.Size.CompareTo(x.Size);
                });
            }

            RaceFile? TakeBest()
            {
                lock (sync)
                {
                    var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                    var now = DateTime.UtcNow;
                    for (var i = 0; i < pending.Count; i++)
                    {
                        var f = pending[i];
                        if (transferred.ContainsKey(f.Rel)) { pending.RemoveAt(i); i--; continue; } // opponent won it
                        if (IsUnreadableSfv(f))
                        {
                            notBefore[f.Rel] = now.AddMilliseconds(250);
                            continue;
                        }
                        if (notBefore.TryGetValue(f.Rel, out var nb) && nb > now) continue;
                        lock (attemptsLock) { if (InBackoff(attempts, f.Rel, nowMs)) continue; }
                        pending.RemoveAt(i);
                        inFlight.Add(f.Rel);
                        return f;
                    }
                    return null;
                }
            }

            void FinishFile(RaceFile f, bool requeue)
            {
                lock (sync)
                {
                    inFlight.Remove(f.Rel);
                    if (requeue) { pending.Add(f); SortPending(); }
                }
                if (requeue) workSignal.Release();
            }

            bool DestinationAlreadyHas(RaceFile f, out long size)
            {
                if (!destinationFiles.TryGetValue(f.Rel, out size)) return false;
                // Only a COMPLETE dest copy counts (size matches source, or source size
                // unknown). A 0-byte/growing file is an opponent's in-flight claim: keep
                // the file in play — the dest lister holds it via notBefore and frees it
                // for a new attempt the moment the claim dies.
                return size > 0 && (f.Size <= 0 || size >= f.Size);
            }

            void RecordSuccess(RaceFile f, DateTime startedAt)
            {
                sentByUs.TryAdd(f.Rel, true);
                transferred.TryAdd(f.Rel, true);
                // With destination precheck disabled this local snapshot is the SFV
                // completion source of truth. A successful final reply confirms that
                // the complete file landed; no extra destination LIST is needed.
                destinationFiles[f.Rel] = Math.Max(1, f.Size);
                uploadBusyRetries.TryRemove(f.Rel, out _);
                Interlocked.Add(ref cumulative, Math.Max(0, f.Size));
                var cum = Interlocked.Read(ref cumulative);
                var now = DateTime.UtcNow;
                lock (speedLock) recentTransfers.Add((startedAt, now, Math.Max(0, f.Size)));
                var speed = CurrentSpeed(now);
                var sent = Interlocked.Increment(ref sentCount);
                _store.UpdateJobTransient(id, j =>
                {
                    j.FilesDone = sent; // files WE won, not the whole release
                    j.CurrentFile = f.Name;
                    j.BytesDone = cum;
                    j.CumulativeBytes = cum;
                    j.SpeedBps = speed;
                });
                NotifyChangedThrottled();
            }

            void UpsertRaceFileRows(IEnumerable<(string Rel, long Size, string Status, string Error)> rows)
            {
                var now = DateTime.UtcNow;
                var list = rows.ToList();
                if (list.Count == 0) return;
                _store.UpdateJobTransient(id, j =>
                {
                    foreach (var item in list)
                    {
                        var row = j.Files.LastOrDefault(x => x.Name.Equals(item.Rel, StringComparison.OrdinalIgnoreCase));
                        if (row is null)
                        {
                            j.Files.Add(new FileTransfer
                            {
                                Name = item.Rel,
                                FromSite = req.FromSite,
                                ToSite = req.ToSite,
                                Size = Math.Max(0, item.Size),
                                StartedAt = now,
                                Status = item.Status,
                                Error = item.Error
                            });
                            continue;
                        }
                        if (row.Status == "done") continue;
                        if (item.Status == "expected" && row.Status is not "expected") continue;
                        if (row.Status == "active" && item.Status == "queued") continue;
                        if (row.Size <= 0 && item.Size > 0) row.Size = item.Size;
                        row.Status = item.Status;
                        row.Error = item.Error;
                    }
                });
                NotifyChangedThrottled();
            }

            async Task<string?> TryReadSfvAsync(RaceFile sfvFile)
            {
                // Tiny 1-byte SFVs show up while the file is announced but not readable
                // on a slave yet. Retrying those every poll hammers PRET/RETR and floods
                // the log, so give the source a short breath before trying again.
                if (sfvFile.Size < 8)
                {
                    nextSfvRead[sfvFile.Rel] = DateTime.UtcNow.AddSeconds(2);
                    return null;
                }

                FtpClient? conn = null;
                using var sfvReadCts = CancellationTokenSource.CreateLinkedTokenSource(stopWorkers.Token);
                sfvReadCts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    conn = await srcPool.BorrowAsync(sfvReadCts.Token).ConfigureAwait(false);
                    var raw = await conn.RetrieveTextAsync(sfvFile.Abs, 1024 * 1024, sfvReadCts.Token).ConfigureAwait(false);
                    srcPool.Return(conn);
                    conn = null;
                    return raw;
                }
                catch (OperationCanceledException) when (stopWorkers.IsCancellationRequested)
                {
                    if (conn is not null) srcPool.Drop(conn);
                    return null;
                }
                catch (OperationCanceledException)
                {
                    if (conn is not null) srcPool.Drop(conn);
                    nextSfvRead[sfvFile.Rel] = DateTime.UtcNow.AddSeconds(3);
                    LogJobLive(id, "warn", $"could not parse {sfvFile.Rel}: timed out");
                    return null;
                }
                catch (Exception ex)
                {
                    if (conn is not null) srcPool.Drop(conn);
                    nextSfvRead[sfvFile.Rel] = DateTime.UtcNow.AddSeconds(3);
                    LogJobLive(id, "warn", $"could not parse {sfvFile.Rel}: {FirstLineOf(ex.Message)}");
                    return null;
                }
            }

            async Task ReadAndParseSfvAsync(RaceFile sfvFile)
            {
                await Task.Yield(); // let GetOrAdd publish this task before it can remove itself
                try
                {
                    var raw = await TryReadSfvAsync(sfvFile).ConfigureAwait(false);
                    if (string.IsNullOrWhiteSpace(raw)) return;

                    var parsed = Sfv.Parse(raw);
                    if (parsed.Count == 0)
                    {
                        nextSfvRead[sfvFile.Rel] = DateTime.UtcNow.AddSeconds(1);
                        LogJobLive(id, "warn", $"could not parse {sfvFile.Rel}: no readable SFV entries yet");
                        return;
                    }

                    var expectedRows = new List<(string Rel, long Size, string Status, string Error)>();
                    foreach (var file in parsed)
                    {
                        var rel = string.IsNullOrEmpty(sfvFile.ParentRel) ? file.Name : sfvFile.ParentRel + "/" + file.Name;
                        var addedExpected = false;
                        lock (sync) addedExpected = expectedFromSfv.Add(rel);
                        if (!addedExpected) continue;
                        sourceSizes.TryGetValue(rel, out var knownSize);
                        expectedRows.Add((rel, knownSize, "expected", $"listed in {sfvFile.Rel}"));
                    }

                    parsedSfvSizes[sfvFile.Rel] = sfvFile.Size;
                    if (expectedRows.Count == 0) return;
                    UpsertRaceFileRows(expectedRows);
                    LogJobLive(id, "info", $"parsed {sfvFile.Rel}: {expectedRows.Count} expected file(s)");
                }
                catch (OperationCanceledException) when (stopWorkers.IsCancellationRequested) { }
                catch (Exception ex)
                {
                    nextSfvRead[sfvFile.Rel] = DateTime.UtcNow.AddSeconds(3);
                    LogJobLive(id, "warn", $"could not parse {sfvFile.Rel}: {FirstLineOf(ex.Message)}");
                }
                finally
                {
                    sfvReads.TryRemove(sfvFile.Rel, out _);
                }
            }

            void QueueSfvRead(RaceFile sfvFile)
            {
                if (parsedSfvSizes.TryGetValue(sfvFile.Rel, out var parsedSize) && parsedSize == sfvFile.Size)
                    return;
                if (nextSfvRead.TryGetValue(sfvFile.Rel, out var retryAt) && retryAt > DateTime.UtcNow)
                    return;
                sfvReads.GetOrAdd(sfvFile.Rel, _ => ReadAndParseSfvAsync(sfvFile));
            }

            bool SnapshotShowsComplete(out string description)
            {
                lock (sync)
                {
                    if (Volatile.Read(ref destinationComplete) != 0)
                    {
                        description = completionDescription;
                        return true;
                    }
                    if (expectedFromSfv.Count == 0)
                    {
                        description = "";
                        return false;
                    }
                    foreach (var rel in expectedFromSfv)
                    {
                        if (!destinationFiles.TryGetValue(rel, out var destSize) || destSize <= 0)
                        {
                            description = "";
                            return false;
                        }
                    }
                    description = "all files listed in SFV are visible";
                    return true;
                }
            }

            string DescribeIncompleteSnapshot()
            {
                lock (sync)
                {
                    if (expectedFromSfv.Count > 0)
                    {
                        var missing = expectedFromSfv.Count(rel =>
                            !destinationFiles.TryGetValue(rel, out var size) || size <= 0);
                        if (missing > 0)
                            return $"race stopped idle: {missing} of {expectedFromSfv.Count} file(s) listed in SFV never appeared on destination";
                    }

                    var unreadableSfv = sourceSizes.Count(item =>
                        item.Value < 8 && item.Key.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));
                    if (unreadableSfv > 0)
                        return $"race stopped idle: {unreadableSfv} SFV file(s) remained incomplete or unreadable on source";

                    return "race stopped idle: no readable SFV or completion marker appeared";
                }
            }

            async Task<bool> TryCompletionProbeAsync()
            {
                FtpClient? probe = null;
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(TimeSpan.FromSeconds(3));
                try
                {
                    probe = await dstPool.TryBorrowAsync(probeCts.Token).ConfigureAwait(false);
                    if (probe is null) return false;
                    var chk = await CheckReleaseOnAsync(probe, dstSite, req.DestPath, persist: false, probeCts.Token).ConfigureAwait(false);
                    dstPool.Return(probe);
                    probe = null;
                    var complete = chk.State == ReleaseState.Complete;
                    if (complete)
                        LogJob(id, "info", $"race complete ({chk.Description})");
                    return complete;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    if (probe is not null) dstPool.Drop(probe);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    if (probe is not null) dstPool.Drop(probe);
                    LogJobLive(id, "warn", "completion check timed out");
                    return false;
                }
                catch (Exception ex)
                {
                    if (probe is not null) dstPool.Drop(probe);
                    var m = FirstLineOf(ex.Message);
                    if (!m.Contains("no such file", StringComparison.OrdinalIgnoreCase) &&
                        !m.Contains("not found", StringComparison.OrdinalIgnoreCase))
                        LogJobLive(id, "warn", $"completion check failed: {m}");
                    return false;
                }
            }

            async Task ListerAsync()
            {
                try
                {
                    while (!ct.IsCancellationRequested && Volatile.Read(ref raceDone) == 0)
                    {
                        var pollCycle = System.Diagnostics.Stopwatch.StartNew();
                        // A failed BORROW (dial refused, "530 too many connections", …)
                        // must never crash the lister — that used to fail the whole race.
                        var files = new List<RaceFile>();
                        FtpClient? lister = null;
                        try
                        {
                            lister = await srcPool.BorrowAsync(ct).ConfigureAwait(false);
                            var listSw = System.Diagnostics.Stopwatch.StartNew();
                            // Root files contain the announce-critical SFV/NFO/RARs. Keep
                            // that STAT hot; refreshing Sample/Proof and other subdirs on
                            // every 25ms poll multiplied the round trips and delayed root
                            // discoveries by up to 100ms+ under load.
                            var includeNested = DateTime.UtcNow >= nextNestedSourceList;
                            files = await ListSourceFilesAsync(lister, req.SourcePath, skiplist, ct,
                                recursive: includeNested).ConfigureAwait(false);
                            if (includeNested)
                            {
                                nestedSourceFiles.Clear();
                                foreach (var nested in files.Where(file => file.ParentRel.Length > 0))
                                    nestedSourceFiles[nested.Rel] = nested;
                                nextNestedSourceList = DateTime.UtcNow.AddMilliseconds(250);
                            }
                            else if (nestedSourceFiles.Count > 0)
                            {
                                files.AddRange(nestedSourceFiles.Values);
                            }
                            Volatile.Write(ref lastSourceListMs, (int)Math.Min(int.MaxValue, listSw.ElapsedMilliseconds));
                            srcPool.Return(lister);
                        }
                        catch (OperationCanceledException) { if (lister is not null) srcPool.Drop(lister); throw; }
                        catch (Exception ex)
                        {
                            if (lister is not null) srcPool.Drop(lister);
                            listFails++;
                            if (listFails == 1 || listFails % 15 == 0)
                                LogJobLive(id, "warn", $"source list failed ({listFails}x): {FirstLineOf(ex.Message)}");
                            files.Clear();
                        }
                        if (files.Count > 0) listFails = 0;

                        poll++;
                        int added = 0, pendingCount, inFlightCount, knownCount;
                        List<RaceFile>? newlyKnown = null;
                        var listedBytes = files.Where(file => file.Size > 0).Sum(file => file.Size);
                        lock (sync)
                        {
                            foreach (var f in files)
                            {
                                sourceSizes[f.Rel] = f.Size;
                                if (transferred.ContainsKey(f.Rel)) continue;
                                bool exceeded;
                                lock (attemptsLock) { exceeded = AttemptsExceeded(attempts, f.Rel); }
                                if (exceeded) continue;
                                if (!known.Add(f.Rel))
                                {
                                    // Still pending? The file may have grown since first
                                    // seen (source mid-upload). A size change is a fresh
                                    // chance to RETR now, not after the previous backoff.
                                    var idx = pending.FindIndex(p => p.Rel.Equals(f.Rel, StringComparison.OrdinalIgnoreCase));
                                    if (idx >= 0 && pending[idx].Size != f.Size)
                                    {
                                        pending[idx] = f;
                                        notBefore.TryRemove(f.Rel, out _);
                                        uploadBusyRetries.TryRemove(f.Rel, out _);
                                        added++;
                                    }
                                    continue;
                                }
                                pending.Add(f);
                                added++;
                                (newlyKnown ??= new List<RaceFile>()).Add(f);
                            }
                            if (added > 0) SortPending();
                            pendingCount = pending.Count;
                            inFlightCount = inFlight.Count;
                            knownCount = known.Count;
                        }
                        if (newlyKnown is { Count: > 0 })
                        {
                            UpsertRaceFileRows(newlyKnown.Select(f => IsUnreadableSfv(f)
                                ? (f.Rel, f.Size, "wait", "SFV is not readable on a source slave yet")
                                : (f.Rel, f.Size, "queued", "seen on source")));
                            foreach (var f in newlyKnown)
                                LogJobLive(id, "info", $"seen {f.Rel} ({HumanBytes(f.Size)}) on source");
                        }

                        foreach (var sfvFile in files.Where(f => f.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && f.Size > 0))
                            QueueSfvRead(sfvFile);
                        if (added > 0) workSignal.Release(Math.Min(added, Math.Max(1, wantSlots))); // wake idle workers NOW

                        var pollLogEvery = Math.Max(30, 5000 / Math.Max(1, pollMs));
                        if (files.Count != lastFound || poll % pollLogEvery == 1)
                        {
                            var srcWaits = Interlocked.Exchange(ref noSrcSlot, 0);
                            var dstWaits = Interlocked.Exchange(ref noDstSlot, 0);
                            var busySinceLast = Interlocked.Exchange(ref uploadBusy, 0);
                            LogJobLive(id, "info", $"poll #{poll}: {files.Count} on source, {pendingCount} queued, {inFlightCount} in flight, {transferred.Count} done · src-list {Volatile.Read(ref lastSourceListMs)}ms · dst-list {Volatile.Read(ref lastDestListMs)}ms · waits src/dst {srcWaits}/{dstWaits} · upload-busy {busySinceLast}");
                            lastFound = files.Count;
                        }
                        // Refresh the speed each poll too, so it decays toward 0 when the
                        // source goes quiet instead of freezing at the last transfer's rate.
                        var liveSpeed = CurrentSpeed(DateTime.UtcNow);
                        _store.UpdateJobTransient(id, j =>
                        {
                            int expectedCount;
                            lock (sync) expectedCount = expectedFromSfv.Count;
                            j.FilesTotal = Math.Max(j.FilesTotal, Math.Max(knownCount, expectedCount));
                            j.BytesTotal = Math.Max(j.BytesTotal, listedBytes);
                            j.BytesDone = Interlocked.Read(ref cumulative);
                            j.SpeedBps = liveSpeed;
                        });

                        // Cheap local completeness check on EVERY poll (no round trip): it
                        // only reads the snapshot we already have. The idle-only gate below
                        // never fired while a single file sat in backoff or "wait", so a
                        // finished release could stay Running for a minute and keep holding
                        // pooled connections that other races needed.
                        if (SnapshotShowsComplete(out var earlyDescription))
                        {
                            LogJob(id, "info", $"race complete ({earlyDescription})");
                            Volatile.Write(ref raceDone, 1);
                            break;
                        }

                        if (added == 0 && pendingCount == 0 && inFlightCount == 0)
                        {
                            idleCycles++;
                            idleSince ??= DateTime.UtcNow;
                            // Completion probe over a BORROWED pooled connection (no
                            // dial+TLS+login per probe). SFV contents are the primary
                            // signal; complete markers remain the fallback for dirs
                            // without an SFV (zips, mp3 subdirs, ...).
                            var complete = SnapshotShowsComplete(out var localDescription);
                            if (complete)
                                LogJob(id, "info", $"race complete ({localDescription})");
                            else if (DateTime.UtcNow >= nextCompletionProbe)
                            {
                                // The local snapshot only knows files this process raced
                                // or learned through X-DUPE. An opponent can complete the
                                // destination before those files ever appear in our source
                                // listing, so an SFV must not suppress the destination probe.
                                nextCompletionProbe = DateTime.UtcNow.AddSeconds(2);
                                complete = await TryCompletionProbeAsync().ConfigureAwait(false);
                            }
                            if (complete)
                            {
                                Volatile.Write(ref raceDone, 1);
                                break;
                            }
                            if (DateTime.UtcNow - idleSince.Value >= idleTimeout)
                            {
                                incompleteReason = DescribeIncompleteSnapshot();
                                LogJob(id, "warn", $"{incompleteReason} after {(DateTime.UtcNow - idleSince.Value).TotalSeconds:0.0}s without new files");
                                Volatile.Write(ref raceDone, 2);
                                break;
                            }
                        }
                        else
                        {
                            idleCycles = 0;
                            idleSince = null;
                        }

                        // RacePollIntervalMs is a start-to-start cadence, not an extra
                        // sleep after STAT. The old code made a configured 25ms poll take
                        // 40-100ms and backed off after only ~125ms of quiet. Keep an
                        // incomplete SFV hot; only races without an SFV back off after
                        // several real seconds with no work.
                        int expectedCountForPolling;
                        lock (sync) expectedCountForPolling = expectedFromSfv.Count;
                        var quietFor = idleSince is null ? TimeSpan.Zero : DateTime.UtcNow - idleSince.Value;
                        var targetCycleMs = added > 0 || inFlightCount > 0 || pendingCount > 0
                            ? Math.Min(pollMs, 10)
                            : expectedCountForPolling > 0 || quietFor < TimeSpan.FromSeconds(5)
                                ? pollMs
                                : quietFor < TimeSpan.FromSeconds(15) ? pollMs * 2 : pollMs * 4;
                        var remainingMs = Math.Min(targetCycleMs, 30000) - (int)pollCycle.ElapsedMilliseconds;
                        try
                        {
                            if (remainingMs > 0) await Task.Delay(remainingMs, ct).ConfigureAwait(false);
                            else await Task.Yield();
                        }
                        catch (OperationCanceledException) { break; }
                    }
                }
                finally
                {
                    if (Volatile.Read(ref raceDone) == 0 && !ct.IsCancellationRequested)
                        Volatile.Write(ref raceDone, 2);
                    stopWorkers.Cancel(); // done (or stopped): wind the workers down
                }
            }

            async Task WorkerAsync()
            {
                var wct = stopWorkers.Token;
                // No dest-setup gate: waiting for one used to be the single biggest delay
                // between the announce and our first STOR (p50 254ms, p90 2.4s), because it
                // had to win a pooled dst connection while other races held them all.
                while (!wct.IsCancellationRequested)
                {
                    try { await WaitWhilePausedAsync(id, wct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }

                    var picked = TakeBest();
                    if (picked is null)
                    {
                        if (Volatile.Read(ref raceDone) != 0) return;
                        // Sleep until the lister queues work (signal) or 200ms passes
                        // (covers notBefore/backoff windows opening up).
                        try { await workSignal.WaitAsync(wakeMs, wct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        continue;
                    }
                    var f = picked.Value;

                    if (DestinationAlreadyHas(f, out var existingSize))
                    {
                        transferred.TryAdd(f.Rel, true);
                        UpsertRaceFileRows(new[]
                        {
                            (f.Rel, f.Size, "dupe", existingSize > 0
                                ? $"already on destination ({HumanBytes(existingSize)})"
                                : "already on destination")
                        });
                        _store.UpdateJobTransient(id, j => j.CurrentFile = f.Name);
                        LogJobLive(id, "info", $"skipped {f.Rel}: already on destination{(existingSize > 0 ? $" ({HumanBytes(existingSize)})" : "")}");
                        FinishFile(f, requeue: false);
                        NotifyChangedThrottled();
                        continue;
                    }

                    // Transfer workers never queue on the login semaphore. Queued workers
                    // used to get ahead of directory listers under load, delaying new-file
                    // discovery by seconds. A worker claims both transfer reservations now,
                    // or immediately puts the file back and waits for an availability signal.
                    FtpClient? s = null, d = null;
                    try
                    {
                        s = await srcPool.TryBorrowTransferAsync(id, asSource: true, wct, NewcomerReservedSlots).ConfigureAwait(false);
                        if (s is null)
                        {
                            Interlocked.Increment(ref noSrcSlot);
                            FinishFile(f, requeue: true);
                            await srcPool.WaitForTransferAvailabilityAsync(id, asSource: true, TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }
                        d = await dstPool.TryBorrowTransferAsync(id, asSource: false, wct, NewcomerReservedSlots).ConfigureAwait(false);
                        if (d is null)
                        {
                            Interlocked.Increment(ref noDstSlot);
                            srcPool.ReturnTransfer(id, asSource: true, s);
                            s = null;
                            FinishFile(f, requeue: true);
                            await dstPool.WaitForTransferAvailabilityAsync(id, asSource: false, TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (d is not null) dstPool.ReturnTransfer(id, asSource: false, d);
                        if (s is not null) srcPool.ReturnTransfer(id, asSource: true, s);
                        FinishFile(f, requeue: false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (d is not null) dstPool.ReturnTransfer(id, asSource: false, d);
                        if (s is not null) srcPool.ReturnTransfer(id, asSource: true, s);
                        FinishFile(f, requeue: true);
                        LogJobLive(id, "warn", $"connect failed: {FirstLineOf(ex.Message)} — retrying");
                        try { await Task.Delay(Math.Max(100, pollMs), wct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                        continue;
                    }

                    var srcOk = true; var dstOk = true; var cancelled = false; var requeue = false;
                    var slowSkipped = false;
                    var xferStart = DateTime.UtcNow;

                    // Per-file row: one entry per attempt, updated with the
                    // outcome + speed snapshot. Failed attempts stay visible.
                    void FileRow(string status, string error = "")
                    {
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == f.Rel && x.Status is "active" or "wait" or "queued");
                            if (row is null)
                            {
                                row = new FileTransfer
                                {
                                    Name = f.Rel,
                                    FromSite = req.FromSite,
                                    ToSite = req.ToSite,
                                    Size = Math.Max(0, f.Size),
                                    StartedAt = xferStart
                                };
                                j.Files.Add(row);
                            }
                            else if (status == "active")
                            {
                                row.StartedAt = xferStart;
                                row.Seconds = 0;
                                row.Bps = 0;
                            }
                            if (status != "active")
                            {
                                row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                                row.Bps = status == "done" ? row.Size / row.Seconds : 0;
                            }
                            row.Status = status;
                            row.Error = error;
                        });
                    }

                    try
                    {
                        await dirSem.WaitAsync(ct).ConfigureAwait(false);
                        try { await EnsureDestDirAsync(d, req.DestPath, f.ParentRel, madeDirs, id).ConfigureAwait(false); }
                        finally { dirSem.Release(); }

                        var absDst = FtpClient.JoinRemote(req.DestPath, f.Rel);
                        if (announced.TryAdd(f.Rel, true))
                            LogJobLive(id, "info", $"sending {f.Rel} ({HumanBytes(f.Size)})");
                        _store.UpdateJobTransient(id, j => j.CurrentFile = f.Name);
                        xferStart = DateTime.UtcNow;
                        FileRow("active");

                        var xfer = FxpTransfer.TransferSingleAsync(s, d, dstCfg, f.Abs, absDst,
                            (level, message) => LogJobLive(id, level, message), ct, dataGate);
                        if (f.Size > 0)
                        {
                            // Slow-skip: FXP bytes don't pass through us, so enforce the
                            // minimum speed as a time budget (size/threshold + grace for
                            // setup/gate). Blown budget => ABOR both sides, move on.
                            // Even with slow-skip OFF a stall guard always runs: a dead
                            // data channel (TLS role deadlock, dropped conn) must never
                            // squat a worker + two connections + a stream slot for the
                            // rest of the race while cbftp keeps racing — assume at
                            // least ~1 MB/s with a 45s floor, abort and retry.
                            var guardKBps = slowKBps > 0 ? slowKBps : 1024;
                            var floor = slowKBps > 0 ? 15 : 45;
                            var budget = TimeSpan.FromSeconds(Math.Max(floor, f.Size / 1024.0 / guardKBps + 10));
                            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            var finished = await Task.WhenAny(xfer, Task.Delay(budget, delayCts.Token)).ConfigureAwait(false);
                            if (finished == xfer) delayCts.Cancel();
                            else
                            {
                                slowSkipped = true;
                                try { await s.NudgeAbortAsync().ConfigureAwait(false); } catch { }
                                try { await d.NudgeAbortAsync().ConfigureAwait(false); } catch { }
                            }
                        }
                        await xfer.ConfigureAwait(false);
                        if (slowSkipped) { srcOk = false; dstOk = false; } // ABOR replies unread — drop conns
                        FileRow("done");
                        RecordSuccess(f, xferStart);
                        var dur = (DateTime.UtcNow - xferStart).TotalSeconds;
                        // Always visible (also for silent API races): per-file duration is
                        // the number that shows where a race is being lost.
                        LogJobLive(id, "info", $"raced {f.Rel} ({HumanBytes(f.Size)}) in {dur:0.00}s");
                        RecordRoutePerformance(req.FromSite, req.ToSite, f.Size / Math.Max(0.001, dur));
                        _store.AddSiteTraffic(req.FromSite, f.Size, 0, dur);
                        _store.AddSiteTraffic(req.ToSite, 0, f.Size, dur);
                    }
                    catch (Exception ex) when (slowSkipped)
                    {
                        // We aborted it for being too slow. Connections carry unread ABOR
                        // replies — drop them. Retry later; counts toward the give-up cap.
                        srcOk = false; dstOk = false;
                        requeue = true;
                        notBefore[f.Rel] = DateTime.UtcNow.AddSeconds(3);
                        var nowMsSlow = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) { RecordFail(attempts, f.Rel, nowMsSlow); }
                        var why = slowKBps > 0 ? $"below {slowKBps} KB/s" : "stalled (no completion within budget)";
                        FileRow("slow", $"aborted: {why} ({FirstLineOf(ex.Message)})");
                        LogJobLive(id, "warn", $"aborted {f.Name}: {why}");
                    }
                    catch (Exception ex) when (FxpTransfer.IsBeingUploaded(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        // The source has announced the file but is still writing it. Keep it
                        // hot: a long retry delay gives another racer the completed file.
                        Interlocked.Increment(ref uploadBusy);
                        requeue = true;
                        // The source announced the name just before closing its upload.
                        // Start hot, then back off while the exact same size remains busy.
                        // A growing source size resets this to the first delay.
                        var delayMs = RegisterUploadBusy(uploadBusyRetries, f.Rel, f.Size);
                        notBefore[f.Rel] = DateTime.UtcNow.AddMilliseconds(delayMs);
                        FileRow("wait", $"still uploading on source; retry in {delayMs}ms");
                        _ = ex;
                    }
                    catch (Exception ex) when (FxpTransfer.TryGetServerSlotLimit(ex, out var downloadLimit, out var serverLimit))
                    {
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var limitedPool = downloadLimit ? srcPool : dstPool;
                        var changed = limitedPool.LimitTransferSlots(downloadLimit, serverLimit);
                        notBefore[f.Rel] = DateTime.UtcNow.AddMilliseconds(250);
                        FileRow("wait", $"server slot limit {serverLimit}; retrying");
                        if (changed)
                            LogJobLive(id, "warn", $"{(downloadLimit ? req.FromSite + " download" : req.ToSite + " upload")} slots reduced to server limit {serverLimit}");
                    }
                    catch (Exception ex) when (FxpTransfer.IsSkippableTransferError(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        uploadBusyRetries.TryRemove(f.Rel, out _);
                        transferred.TryAdd(f.Rel, true); // already on dest / -missing / dupe
                        if (FxpTransfer.IsDestinationDupeError(ex))
                            destinationFiles[f.Rel] = Math.Max(1, f.Size);
                        // X-DUPE replies list OTHER files already on the dest in this dir —
                        // learn the whole batch from one refusal instead of paying a failed
                        // STOR round trip for each.
                        var learned = new List<(string Rel, long Size, string Status, string Error)>();
                        foreach (var dupeName in FxpTransfer.ParseXdupeNames(ex))
                        {
                            var rel = string.IsNullOrEmpty(f.ParentRel) ? dupeName : f.ParentRel + "/" + dupeName;
                            if (SkiplistMatches(rel, dupeName, skiplist)) continue;
                            sourceSizes.TryGetValue(rel, out var learnedSize);
                            destinationFiles[rel] = Math.Max(1, learnedSize);
                            if (transferred.TryAdd(rel, true))
                            {
                                learned.Add((rel, learnedSize, "dupe", "learned via X-DUPE"));
                            }
                        }
                        UpsertRaceFileRows(learned);
                        FileRow("dupe", FirstLineOf(ex.Message));
                        foreach (var row in learned)
                            LogJobLive(id, "info", $"skipped {row.Rel}: dupe (learned via X-DUPE)");
                        LogJobLive(id, "info", $"skipped {f.Name}: dupe");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        cancelled = true; srcOk = false; dstOk = false;
                        FileRow("fail", "stopped");
                    }
                    catch (Exception ex)
                    {
                        srcOk = false; dstOk = false; // connection may be broken — discard it
                        FileRow("fail", FirstLineOf(ex.Message));
                        int count;
                        var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) { RecordFail(attempts, f.Rel, nowMs); count = attempts[f.Rel].Count; }
                        if (count >= 7)
                            LogJob(id, "error", $"giving up on {f.Name} after {count} attempts: {FirstLineOf(ex.Message)}");
                        else
                        {
                            requeue = true; // retried after backoff without waiting for a poll
                            LogJobLive(id, "warn", $"transfer failed for {f.Name} (attempt {count}): {FirstLineOf(ex.Message)}");
                        }
                    }
                    finally
                    {
                        if (srcOk) srcPool.ReturnTransfer(id, asSource: true, s); else srcPool.DropTransfer(id, asSource: true, s);
                        if (dstOk) dstPool.ReturnTransfer(id, asSource: false, d); else dstPool.DropTransfer(id, asSource: false, d);
                        FinishFile(f, requeue && !cancelled);
                    }
                    if (cancelled) return;
                }
            }

            // Destination lister. Files already
            // on the dest (won by another racer, or partial mid-upload by them) are
            // unwinnable; marking them transferred means we never waste a slot on a
            // doomed STOR round trip.
            async Task DestListerAsync()
            {
                // Listing a dir that doesn't exist yet just fails harmlessly and is
                // retried on the next cycle.
                while (!ct.IsCancellationRequested && Volatile.Read(ref raceDone) == 0)
                {
                    FtpClient? conn = null;
                    try { conn = await dstPool.TryBorrowAsync(ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    catch { conn = null; } // dial refused — try again next cycle
                    if (conn is not null)
                    {
                        try
                        {
                            var listSw = System.Diagnostics.Stopwatch.StartNew();
                            var have = await ListSourceFilesAsync(conn, req.DestPath, skiplist, ct).ConfigureAwait(false);
                            Volatile.Write(ref lastDestListMs, (int)Math.Min(int.MaxValue, listSw.ElapsedMilliseconds));
                            dstPool.Return(conn);
                            conn = null;
                            List<(string Rel, long Size, string Status, string Error)>? newlyOwnedRows = null;
                            lock (sync)
                            {
                                var newlyOwned = 0;
                                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var h in have)
                                {
                                    present.Add(h.Rel);
                                    destinationFiles[h.Rel] = h.Size;
                                    if (completeMarkers.Any(marker => CompletionMarkerMatches(h.Name, marker)))
                                    {
                                        completionDescription = $"completion marker {h.Name} visible";
                                        Volatile.Write(ref destinationComplete, 1);
                                    }
                                    // Concede ONLY files that are COMPLETE on dest (size
                                    // matches source, or source size unknown). A 0-byte /
                                    // growing file is just an opponent's in-flight claim —
                                    // conceding it forever hands them the whole release;
                                    // instead hold off briefly so we don't hammer 553s,
                                    // and contest it again if their transfer dies.
                                    var complete = h.Size > 0 &&
                                        (!sourceSizes.TryGetValue(h.Rel, out var ssz) || ssz <= 0 || h.Size >= ssz);
                                    if (complete)
                                    {
                                        if (!inFlight.Contains(h.Rel) && transferred.TryAdd(h.Rel, true))
                                        {
                                            newlyOwned++;
                                            sourceSizes.TryGetValue(h.Rel, out var sourceSize);
                                            newlyOwnedRows ??= new List<(string Rel, long Size, string Status, string Error)>();
                                            newlyOwnedRows.Add((h.Rel, sourceSize > 0 ? sourceSize : h.Size, "dupe",
                                                h.Size > 0 ? $"already on destination ({HumanBytes(h.Size)})" : "already on destination"));
                                        }
                                    }
                                    else if (!transferred.ContainsKey(h.Rel) && !inFlight.Contains(h.Rel))
                                    {
                                        notBefore[h.Rel] = DateTime.UtcNow.AddMilliseconds(750);
                                    }
                                }
                                foreach (var rel in destinationFiles.Keys)
                                {
                                    if (present.Contains(rel) || sentByUs.ContainsKey(rel) || inFlight.Contains(rel)) continue;
                                    destinationFiles.TryRemove(rel, out _);
                                }
                                // Un-concede claims that VANISHED from dest (opponent's
                                // upload failed / 0-byte cleaned up) — cbftp re-races
                                // these too via its continuous list comparison.
                                foreach (var rel in transferred.Keys)
                                {
                                    if (present.Contains(rel) || sentByUs.ContainsKey(rel) || inFlight.Contains(rel)) continue;
                                    if (transferred.TryRemove(rel, out _))
                                    {
                                        destinationFiles.TryRemove(rel, out _);
                                        known.Remove(rel); // source lister re-queues it next poll
                                    }
                                }
                                if (newlyOwned > 0)
                                    pending.RemoveAll(p => transferred.ContainsKey(p.Rel));
                            }
                            if (newlyOwnedRows is { Count: > 0 })
                            {
                                UpsertRaceFileRows(newlyOwnedRows);
                                foreach (var row in newlyOwnedRows)
                                    LogJobLive(id, "info", $"skipped {row.Rel}: already on destination");
                            }
                        }
                        catch (OperationCanceledException) { if (conn is not null) dstPool.Drop(conn); return; }
                        catch { if (conn is not null) dstPool.Drop(conn); } // dest dir may not exist yet — fine
                    }
                    try { await Task.Delay(Math.Max(pollMs, 250), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            var listerTask = ListerAsync();
            var workerTasks = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToList();
            workerTasks.Add(listerTask);
            if (destinationPrecheck)
                workerTasks.Add(DestListerAsync());
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            var remainingSfvReads = sfvReads.Values.ToArray();
            if (remainingSfvReads.Length > 0)
                await Task.WhenAll(remainingSfvReads).ConfigureAwait(false);
            var complete = Volatile.Read(ref raceDone) == 1;
            return (complete, complete ? "" : incompleteReason);
        }
        finally
        {
            ReleasePool(req.FromSite);
            ReleasePool(req.ToSite);
        }
    }

    public List<SiteHourStat> SiteStats() => _store.SiteStats();

    // ---- shared per-site connection pools ---------------------------------------------

    private readonly object _poolLock = new();
    private readonly Dictionary<string, SitePool> _pools = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _poolRefs = new(StringComparer.OrdinalIgnoreCase);

    private SitePool AcquirePool(string name, Site site, FtpClient.Config cfg)
    {
        lock (_poolLock)
        {
            // Never exceed the site's login limit: one conn over it means a guaranteed
            // "530 too many connections" churn at the busiest moment. With an explicit
            // login limit the natural headroom (logins > transfer slots) covers the
            // lister; only the fallback path adds +1.
            var max = site.LoginSlots > 0
                ? site.LoginSlots
                : Math.Max(3, Math.Max(site.DownloadSlots, site.UploadSlots)) + 1;
            max = Math.Clamp(max, 1, 40);
            var sourceMax = Math.Min(max - 1, ResolveSiteSlots(site.DownloadSlots, site));
            var destinationMax = Math.Min(max - 1, ResolveSiteSlots(site.UploadSlots, site));
            var fp = PoolFingerprint(cfg, max, sourceMax, destinationMax);
            _pools.TryGetValue(name, out var pool);
            if (pool is not null && pool.Fingerprint != fp &&
                (!_poolRefs.TryGetValue(name, out var refs) || refs <= 0))
            {
                // Site settings changed while the pool sat idle: rebuild with new config.
                pool.DisposeAll();
                _pools.Remove(name);
                pool = null;
            }
            if (pool is null)
            {
                pool = new SitePool(cfg, max, sourceMax, destinationMax, fp);
                _pools[name] = pool;
            }
            _poolRefs[name] = (_poolRefs.TryGetValue(name, out var n) ? n : 0) + 1;
            return pool;
        }
    }

    private static string PoolFingerprint(FtpClient.Config c, int max, int sourceMax, int destinationMax) =>
        string.Join('|', c.Host, c.Port, c.Username, c.Password, c.TlsMode, c.UseEpsv, c.UsePret, c.UseSscn,
            c.FxpMode, c.PassiveHost, c.ListCommand, c.ForceBinary, c.BrokenPasv, c.UseXdupe, c.XdupeMode,
            c.TimeoutSeconds, c.CwdBeforeStatListing, c.Proxy, c.ProxyUsername, c.ProxyPassword,
            c.DataProxy, c.DataProxyUsername, c.DataProxyPassword, max, sourceMax, destinationMax);

    private void ReleasePool(string name)
    {
        lock (_poolLock)
        {
            if (!_poolRefs.TryGetValue(name, out var n)) return;
            n--;
            if (n <= 0) _poolRefs.Remove(name);
            else _poolRefs[name] = n;
        }
        // The pool itself STAYS, warm connections included — cbftp keeps its site slots
        // permanently logged in, and the next announce must fire its first STOR within
        // milliseconds instead of paying TCP+TLS+login per connection first. The sweep
        // timer NOOPs idle conns and prunes them only after long inactivity.
    }

    // A capped pool of warm FTP connections to one site, shared by all races that use
    // that site. The semaphore caps concurrent in-use connections at the site's login
    // limit; idle connections are kept warm for reuse (no re-login churn).
    internal sealed class SitePool
    {
        public FtpClient.Config Cfg { get; }
        private readonly SemaphoreSlim _gate;
        // Unlike _gate (concurrent borrowers), this permit stays held for the full
        // lifetime of a physical FTP session. Without it overlapping warmups could
        // leave more logged-in idle clients than the site's configured login limit.
        private readonly SemaphoreSlim _openGate;
        private readonly SemaphoreSlim _transferGate;
        private readonly SemaphoreSlim _sourceTransferGate;
        private readonly SemaphoreSlim _destinationTransferGate;
        private readonly int _transferMax;
        private readonly int _sourceTransferMax;
        private readonly int _destinationTransferMax;
        private int _sourceTransferLimit;
        private int _destinationTransferLimit;
        private readonly object _transferOwnerLock = new();
        private readonly Dictionary<string, int> _activeTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _activeSourceTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _activeDestinationTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        // Total transfer connections currently held, maintained under _transferOwnerLock
        // so the newcomer reserve can be evaluated atomically with the per-owner counts.
        private int _activeTransferTotal;
        private int _activeSourceTransferTotal;
        private int _activeDestinationTransferTotal;
        private readonly Dictionary<string, int> _waitingTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _waitingSourceTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _waitingDestinationTransferOwners = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshSourceDemand = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _meshDestinationDemand = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim _warmupGate = new(1, 1);
        private readonly SemaphoreSlim _sweepGate = new(1, 1);
        private readonly ConcurrentBag<(FtpClient Client, DateTime ReturnedUtc)> _idle = new();
        // One wake token per returned connection. The previous TaskCompletionSource
        // broadcast woke every worker across every race for one free slot, causing a
        // thundering herd under concurrent announces.
        private readonly SemaphoreSlim _availability = new(0);
        private static readonly TimeSpan IdleValidateAfter = TimeSpan.FromSeconds(15);
        private const int ReservedControlSlots = 1;

        public int Max { get; }
        public string Fingerprint { get; }

        public SitePool(FtpClient.Config cfg, int max, int sourceMax, int destinationMax, string fingerprint = "")
        {
            Cfg = cfg;
            Max = Math.Max(1, max);
            _gate = new SemaphoreSlim(Max, Max);
            _openGate = new SemaphoreSlim(Max, Max);
            var transferMax = Math.Max(1, Max - Math.Min(ReservedControlSlots, Max - 1));
            _transferMax = transferMax;
            _transferGate = new SemaphoreSlim(transferMax, transferMax);
            _sourceTransferMax = Math.Clamp(sourceMax, 1, transferMax);
            _destinationTransferMax = Math.Clamp(destinationMax, 1, transferMax);
            _sourceTransferLimit = _sourceTransferMax;
            _destinationTransferLimit = _destinationTransferMax;
            _sourceTransferGate = new SemaphoreSlim(_sourceTransferMax, _sourceTransferMax);
            _destinationTransferGate = new SemaphoreSlim(_destinationTransferMax, _destinationTransferMax);
            Fingerprint = fingerprint;
        }

        public event Action? AvailabilityChanged;

        private void SignalAvailability()
        {
            _availability.Release();
            AvailabilityChanged?.Invoke();
        }

        public void SetMeshDemand(string owner, bool source, bool destination)
        {
            lock (_transferOwnerLock)
            {
                if (source && _meshSourceDemand.Add(owner)) AddTransferWaiter(owner, true);
                if (!source && _meshSourceDemand.Remove(owner)) RemoveTransferWaiter(owner, true);
                if (destination && _meshDestinationDemand.Add(owner)) AddTransferWaiter(owner, false);
                if (!destination && _meshDestinationDemand.Remove(owner)) RemoveTransferWaiter(owner, false);
            }
        }

        // Keep idle connections LOGGED IN between races: NOOP the ones idle long enough
        // for the daemon's idle timer to matter, dispose the dead and the long-unused.
        public async Task SweepAsync(TimeSpan idleTtl, TimeSpan keepAliveAfter)
        {
            if (!_sweepGate.Wait(0)) return;
            try
            {
                var keep = new List<(FtpClient Client, DateTime ReturnedUtc)>();
                while (_gate.Wait(0))
                {
                    if (!_idle.TryTake(out var warm))
                    {
                        _gate.Release();
                        break;
                    }

                    var age = DateTime.UtcNow - warm.ReturnedUtc;
                    if (age > idleTtl)
                    {
                        try { warm.Client.Dispose(); } catch { }
                        _openGate.Release();
                        _gate.Release();
                        continue;
                    }
                    if (age > keepAliveAfter)
                    {
                        if (!await warm.Client.TryNoopAsync().ConfigureAwait(false))
                        {
                            try { warm.Client.Dispose(); } catch { }
                            _openGate.Release();
                            _gate.Release();
                            continue;
                        }
                        warm = (warm.Client, DateTime.UtcNow);
                    }
                    keep.Add(warm);
                }
                foreach (var k in keep)
                {
                    _idle.Add(k);
                    _gate.Release();
                }
            }
            finally { _sweepGate.Release(); }
        }

        // Wait for a slot (used for listing / dir setup that must happen).
        public async Task<FtpClient> BorrowAsync(CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            return await TakeOrOpenAsync(ct).ConfigureAwait(false);
        }

        // Take a slot only if one is free right now, else null (used by transfer workers
        // so an idle race never blocks a busy one).
        //
        public async Task<FtpClient?> TryBorrowAsync(CancellationToken ct)
        {
            if (!_gate.Wait(0)) return null;
            return await TakeOrOpenAsync(ct).ConfigureAwait(false);
        }

        // reserve keeps N transfer slots available for races that hold no connection yet
        // (see TryReserveTransferOwner): cbftp's cross-race priority in miniature, so a
        // newly announced release's sfv/nfo never queues behind another race's bulk rars.
        public async Task<FtpClient?> TryBorrowTransferAsync(string owner, bool asSource, CancellationToken ct, int reserve = 0)
        {
            using var reservation = TryReserveTransferSlot(owner, asSource, reserve);
            return reservation is null ? null : await reservation.OpenAsync(ct).ConfigureAwait(false);
        }

        // Claim both sites before dialing either side. Unopened reservations return
        // all permits; opened ones hand their permits to ReturnTransfer/DropTransfer.
        public sealed class TransferReservation : IDisposable
        {
            private readonly SitePool _pool;
            private readonly string _owner;
            private readonly bool _asSource;
            private int _state; // 0 reserved, 1 opening, 2 handed to client, 3 released

            internal TransferReservation(SitePool pool, string owner, bool asSource)
            { _pool = pool; _owner = owner; _asSource = asSource; }

            public async Task<FtpClient> OpenAsync(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                    throw new InvalidOperationException("Transfer reservation already consumed");
                try
                {
                    var client = await _pool.TakeOrOpenAsync(ct).ConfigureAwait(false);
                    Volatile.Write(ref _state, 2);
                    return client;
                }
                catch
                {
                    Volatile.Write(ref _state, 3);
                    // TakeOrOpenAsync returned the login permit on failure.
                    _pool.ReleaseTransferReservation(_owner, _asSource, false);
                    throw;
                }
            }

            public void Dispose()
            {
                if (Interlocked.CompareExchange(ref _state, 3, 0) == 0)
                    _pool.ReleaseTransferReservation(_owner, _asSource, true);
            }
        }

        private void ReleaseTransferReservation(string owner, bool asSource, bool returnLogin)
        {
            ReleaseTransferOwner(owner, asSource);
            if (returnLogin) _gate.Release();
            _transferGate.Release();
            (asSource ? _sourceTransferGate : _destinationTransferGate).Release();
            SignalAvailability();
        }

        public TransferReservation? TryReserveTransferSlot(string owner, bool asSource, int reserve = 0)
        {
            AddTransferWaiter(owner, asSource);
            var directionGate = asSource ? _sourceTransferGate : _destinationTransferGate;
            try
            {
                if (!directionGate.Wait(0)) return null;
                if (!_transferGate.Wait(0))
                {
                    directionGate.Release();
                    return null;
                }
                if (!TryReserveTransferOwner(owner, asSource, reserve))
                {
                    _transferGate.Release();
                    directionGate.Release();
                    SignalAvailability();
                    return null;
                }
                if (!_gate.Wait(0))
                {
                    ReleaseTransferOwner(owner, asSource);
                    _transferGate.Release();
                    directionGate.Release();
                    return null;
                }
                return new TransferReservation(this, owner, asSource);
            }
            finally { RemoveTransferWaiter(owner, asSource); }
        }

        public async Task<FtpClient> BorrowTransferAsync(string owner, bool asSource, CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var conn = await TryBorrowTransferAsync(owner, asSource, ct).ConfigureAwait(false);
                if (conn is not null) return conn;
                await WaitForTransferAvailabilityAsync(owner, asSource, TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false);
            }
        }

        public int TransferCapacity(bool asSource)
        {
            lock (_transferOwnerLock)
                return Math.Max(1, asSource ? _sourceTransferLimit : _destinationTransferLimit);
        }

        // How many more transfers this pool can start right now in the given direction,
        // bounded by directional limit, overall limit and the live connection gates. Used
        // by the global scoreboard to reserve exactly the slots higher-priority races need
        // instead of reserving the whole pool on first touch (which left slots idle).
        public int FreeTransferSlots(bool asSource)
        {
            var directionGate = asSource ? _sourceTransferGate : _destinationTransferGate;
            lock (_transferOwnerLock)
            {
                var directionalMax = asSource ? _sourceTransferLimit : _destinationTransferLimit;
                var directionalTotal = asSource ? _activeSourceTransferTotal : _activeDestinationTransferTotal;
                var free = Math.Min(directionalMax - directionalTotal, _transferMax - _activeTransferTotal);
                free = Math.Min(free, directionGate.CurrentCount);
                free = Math.Min(free, _transferGate.CurrentCount);
                free = Math.Min(free, _gate.CurrentCount);
                return Math.Max(0, free);
            }
        }

        public async Task WaitForTransferAvailabilityAsync(string owner, bool asSource, TimeSpan timeout, CancellationToken ct)
        {
            AddTransferWaiter(owner, asSource);
            try { await _availability.WaitAsync(timeout, ct).ConfigureAwait(false); }
            finally { RemoveTransferWaiter(owner, asSource); }
        }

        // Advisory scheduler check. The actual borrow remains atomic, but checking the
        // same owner/fairness limits before a mesh pick prevents workers from reserving
        // work for a saturated route while another site pair is immediately runnable.
        public bool CanBorrowTransfer(string owner, bool asSource, int reserve = 0)
        {
            var directionGate = asSource ? _sourceTransferGate : _destinationTransferGate;
            if (directionGate.CurrentCount <= 0 || _transferGate.CurrentCount <= 0 || _gate.CurrentCount <= 0)
                return false;
            owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            lock (_transferOwnerLock)
                return CanReserveTransferOwnerLocked(owner, asSource, reserve);
        }

        // Preserve full speed for a single race. Fair sharing only activates when a
        // different race is actually waiting for this site; existing streams are never
        // interrupted, but the busy owner cannot immediately reclaim more than its share.
        private bool TryReserveTransferOwner(string owner, bool asSource, int reserve)
        {
            owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            lock (_transferOwnerLock)
            {
                if (!CanReserveTransferOwnerLocked(owner, asSource, reserve)) return false;
                _activeTransferOwners.TryGetValue(owner, out var activeForOwner);
                var directionalActive = asSource ? _activeSourceTransferOwners : _activeDestinationTransferOwners;
                directionalActive.TryGetValue(owner, out var directionalForOwner);
                _activeTransferOwners[owner] = activeForOwner + 1;
                _activeTransferTotal++;
                directionalActive[owner] = directionalForOwner + 1;
                if (asSource) _activeSourceTransferTotal++;
                else _activeDestinationTransferTotal++;
                return true;
            }
        }

        private bool CanReserveTransferOwnerLocked(string owner, bool asSource, int reserve)
        {
            _activeTransferOwners.TryGetValue(owner, out var activeForOwner);
            var hasCompetingWaiter = _waitingTransferOwners.Any(x => x.Value > 0 &&
                !x.Key.Equals(owner, StringComparison.OrdinalIgnoreCase));
            if (hasCompetingWaiter)
            {
                var demandOwners = 1;
                foreach (var active in _activeTransferOwners)
                    if (active.Value > 0 && !active.Key.Equals(owner, StringComparison.OrdinalIgnoreCase))
                        demandOwners++;
                foreach (var waiting in _waitingTransferOwners)
                    if (waiting.Value > 0 &&
                        !waiting.Key.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                        !_activeTransferOwners.ContainsKey(waiting.Key))
                        demandOwners++;

                var fairLimit = Math.Max(1, (int)Math.Ceiling(_transferMax / (double)demandOwners));
                if (activeForOwner >= fairLimit) return false;
            }
            // Scale the newcomer reserve to the pool: on a 20-slot site keeping 2 free
            // is cheap, but on a 3-slot site (HUSH) a flat reserve of 2 forces every
            // race down to ONE concurrent transfer. Never reserve more than half the
            // slots minus the one we are about to take.
            var overallReserve = Math.Min(reserve, Math.Max(0, (_transferMax - 1) / 2));
            var hasNewcomer = _waitingTransferOwners.Any(x => x.Value > 0 &&
                !x.Key.Equals(owner, StringComparison.OrdinalIgnoreCase) && !_activeTransferOwners.ContainsKey(x.Key));
            if (hasNewcomer && overallReserve > 0 && activeForOwner > 0 && _transferMax - _activeTransferTotal <= overallReserve)
                return false;

            var directionalActive = asSource ? _activeSourceTransferOwners : _activeDestinationTransferOwners;
            var directionalWaiting = asSource ? _waitingSourceTransferOwners : _waitingDestinationTransferOwners;
            var directionalMax = asSource ? _sourceTransferLimit : _destinationTransferLimit;
            var directionalTotal = asSource ? _activeSourceTransferTotal : _activeDestinationTransferTotal;
            if (directionalTotal >= directionalMax) return false;
            directionalActive.TryGetValue(owner, out var directionalForOwner);
            var hasDirectionalCompetitor = directionalWaiting.Any(x => x.Value > 0 &&
                !x.Key.Equals(owner, StringComparison.OrdinalIgnoreCase));
            if (hasDirectionalCompetitor)
            {
                var demandOwners = 1;
                foreach (var active in directionalActive)
                    if (active.Value > 0 && !active.Key.Equals(owner, StringComparison.OrdinalIgnoreCase))
                        demandOwners++;
                foreach (var waiting in directionalWaiting)
                    if (waiting.Value > 0 &&
                        !waiting.Key.Equals(owner, StringComparison.OrdinalIgnoreCase) &&
                        !directionalActive.ContainsKey(waiting.Key))
                        demandOwners++;

                var fairLimit = Math.Max(1, (int)Math.Ceiling(directionalMax / (double)demandOwners));
                if (directionalForOwner >= fairLimit) return false;
            }
            var directionalReserve = Math.Min(reserve, Math.Max(0, (directionalMax - 1) / 2));
            var hasDirectionalNewcomer = directionalWaiting.Any(x => x.Value > 0 &&
                !x.Key.Equals(owner, StringComparison.OrdinalIgnoreCase) && !directionalActive.ContainsKey(x.Key));
            return !hasDirectionalNewcomer || directionalReserve <= 0 || directionalForOwner <= 0 ||
                directionalMax - directionalTotal > directionalReserve;
        }

        public bool LimitTransferSlots(bool asSource, int serverLimit)
        {
            lock (_transferOwnerLock)
            {
                var configuredMax = asSource ? _sourceTransferMax : _destinationTransferMax;
                var learned = Math.Clamp(serverLimit, 1, configuredMax);
                if (asSource)
                {
                    if (learned >= _sourceTransferLimit) return false;
                    _sourceTransferLimit = learned;
                }
                else
                {
                    if (learned >= _destinationTransferLimit) return false;
                    _destinationTransferLimit = learned;
                }
                SignalAvailability();
                return true;
            }
        }

        private void ReleaseTransferOwner(string owner, bool asSource)
        {
            owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            lock (_transferOwnerLock)
            {
                if (!_activeTransferOwners.TryGetValue(owner, out var active)) return;
                if (active <= 1) _activeTransferOwners.Remove(owner);
                else _activeTransferOwners[owner] = active - 1;
                if (_activeTransferTotal > 0) _activeTransferTotal--;

                var directionalActive = asSource ? _activeSourceTransferOwners : _activeDestinationTransferOwners;
                if (directionalActive.TryGetValue(owner, out var directional))
                {
                    if (directional <= 1) directionalActive.Remove(owner);
                    else directionalActive[owner] = directional - 1;
                    if (asSource && _activeSourceTransferTotal > 0) _activeSourceTransferTotal--;
                    if (!asSource && _activeDestinationTransferTotal > 0) _activeDestinationTransferTotal--;
                }
            }
        }

        private void AddTransferWaiter(string owner, bool? asSource = null)
        {
            owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            lock (_transferOwnerLock)
            {
                _waitingTransferOwners[owner] = (_waitingTransferOwners.TryGetValue(owner, out var count) ? count : 0) + 1;
                if (asSource.HasValue)
                {
                    var directional = asSource.Value ? _waitingSourceTransferOwners : _waitingDestinationTransferOwners;
                    directional[owner] = (directional.TryGetValue(owner, out var directionalCount) ? directionalCount : 0) + 1;
                }
            }
        }

        private void RemoveTransferWaiter(string owner, bool? asSource = null)
        {
            owner = string.IsNullOrWhiteSpace(owner) ? "unknown" : owner;
            lock (_transferOwnerLock)
            {
                if (!_waitingTransferOwners.TryGetValue(owner, out var count)) return;
                if (count <= 1) _waitingTransferOwners.Remove(owner);
                else _waitingTransferOwners[owner] = count - 1;
                if (asSource.HasValue)
                {
                    var directional = asSource.Value ? _waitingSourceTransferOwners : _waitingDestinationTransferOwners;
                    if (directional.TryGetValue(owner, out var directionalCount))
                    {
                        if (directionalCount <= 1) directional.Remove(owner);
                        else directional[owner] = directionalCount - 1;
                    }
                }
            }
        }

        private async Task<FtpClient> TakeOrOpenAsync(CancellationToken ct)
        {
            // Consume a matching wake hint when this borrower already claimed the real
            // gate permit. Stale hints are harmless but draining keeps retry loops quiet.
            _availability.Wait(0);
            // Reuse warm logged-in connections; NOOP-validate ones that sat idle long
            // enough for the server to have possibly dropped them, discard the dead.
            while (_idle.TryTake(out var warm))
            {
                if (DateTime.UtcNow - warm.ReturnedUtc < IdleValidateAfter) return warm.Client;
                if (await warm.Client.TryNoopAsync().ConfigureAwait(false)) return warm.Client;
                try { warm.Client.Dispose(); } catch { }
                _openGate.Release();
            }
            var openedPermit = false;
            try
            {
                await _openGate.WaitAsync(ct).ConfigureAwait(false);
                openedPermit = true;
                var c = await FtpClient.DialAndLoginAsync(Cfg, ct).ConfigureAwait(false);
                if (Cfg.UseXdupe) { try { await c.MaybeXdupeAsync().ConfigureAwait(false); } catch { } }
                return c;
            }
            catch
            {
                if (openedPermit) _openGate.Release();
                _gate.Release();
                throw;
            }
        }

        // Open up to `count` connections concurrently and park them as idle, so the
        // first transfers don't pay dial+TLS+login latency one at a time.
        public async Task WarmUpAsync(int count, CancellationToken ct)
        {
            await _warmupGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Target total physical sessions, not just the current idle count.
                // Active transfers already contribute warm capacity for the next race.
                var warmTarget = Math.Min(count, Math.Max(1, Max - Math.Min(ReservedControlSlots, Max - 1)));
                var physical = Max - _openGate.CurrentCount;
                var need = Math.Max(0, warmTarget - physical);
                if (need <= 0) return;
                // A cold 20-slot site used to launch every TLS/login at once. That
                // stalls STAT/PRET replies on the announce-critical connections. Fill
                // the pool in small waves; ready sessions become borrowable per wave.
                const int dialBatchSize = 4;
                for (var offset = 0; offset < need; offset += dialBatchSize)
                {
                    var batchSize = Math.Min(dialBatchSize, need - offset);
                    var dials = Enumerable.Range(0, batchSize).Select(async _ =>
                    {
                        if (!_gate.Wait(0)) return;
                        if (!_openGate.Wait(0)) { _gate.Release(); return; }
                        try
                        {
                            var c = await FtpClient.DialAndLoginAsync(Cfg, ct).ConfigureAwait(false);
                            if (Cfg.UseXdupe) { try { await c.MaybeXdupeAsync().ConfigureAwait(false); } catch { } }
                            Return(c);
                        }
                        catch
                        {
                            _openGate.Release();
                            _gate.Release();
                        }
                    });
                    await Task.WhenAll(dials).ConfigureAwait(false);
                }
            }
            finally { _warmupGate.Release(); }
        }

        public void Return(FtpClient c) { _idle.Add((c, DateTime.UtcNow)); _gate.Release(); SignalAvailability(); }
        public void Drop(FtpClient c) { try { c.Dispose(); } catch { } _openGate.Release(); _gate.Release(); SignalAvailability(); }
        public void ReturnTransfer(string owner, bool asSource, FtpClient c) { _idle.Add((c, DateTime.UtcNow)); ReleaseTransferOwner(owner, asSource); _gate.Release(); _transferGate.Release(); (asSource ? _sourceTransferGate : _destinationTransferGate).Release(); SignalAvailability(); }
        public void DropTransfer(string owner, bool asSource, FtpClient c) { try { c.Dispose(); } catch { } ReleaseTransferOwner(owner, asSource); _openGate.Release(); _gate.Release(); _transferGate.Release(); (asSource ? _sourceTransferGate : _destinationTransferGate).Release(); SignalAvailability(); }
        public void DisposeAll()
        {
            while (_idle.TryTake(out var e))
            {
                try { e.Client.Dispose(); } catch { }
                _openGate.Release();
            }
        }
    }

    // Slots per race = min(source download slots, destination upload slots).
    // As in cbftp, 0 means ALL and resolves to the site's login-slot limit.
    private static int ResolveRaceSlots(Site srcSite, Site dstSite)
    {
        var srcSlots = ResolveSiteSlots(srcSite.DownloadSlots, srcSite);
        var dstSlots = ResolveSiteSlots(dstSite.UploadSlots, dstSite);
        return Math.Clamp(Math.Min(srcSlots, dstSlots), 1, 30);
    }

    private static int ResolveSiteSlots(int configured, Site site)
    {
        var loginLimit = site.LoginSlots > 0 ? site.LoginSlots : 30;
        return configured <= 0
            ? Math.Clamp(loginLimit, 1, 30)
            : Math.Clamp(Math.Min(configured, loginLimit), 1, 30);
    }

    // Recursively list files under root over an open source connection, skipping
    // directories/links traversal control, skiplist matches and -missing markers.
    private async Task<List<RaceFile>> ListSourceFilesAsync(
        FtpClient src, string root, List<string> skiplist, CancellationToken ct, bool recursive = true,
        IReadOnlyList<string>? completeMarkers = null, Action<string>? onCompletionMarker = null,
        bool throwOnRootFailure = false)
    {
        var result = new List<RaceFile>();
        await WalkAsync(src, root, "", 0).ConfigureAwait(false);
        return result;

        async Task WalkAsync(FtpClient client, string absDir, string relDir, int depth)
        {
            if (depth > 16 || ct.IsCancellationRequested) return;
            List<RemoteEntry> entries;
            try
            {
                entries = await client.ListAsync(absDir, ct).ConfigureAwait(false);
            }
            catch when (!throwOnRootFailure || depth > 0)
            {
                // A subdir we can't enter (glftpd tag/status dir, race-condition removal,
                // permission) shouldn't abort the whole walk — just skip it silently.
                return;
            }
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                if (completeMarkers is not null && completeMarkers.Any(marker => CompletionMarkerMatches(e.Name, marker)))
                    onCompletionMarker?.Invoke(e.Name);
                var childAbs = FtpClient.JoinRemote(absDir, e.Name);
                var childRel = relDir.Length == 0 ? e.Name : relDir + "/" + e.Name;
                if (e.Type is "dir" or "link")
                {
                    if (IsVirtualDir(e.Name)) continue;                 // glftpd status/tag "dirs"
                    if (SkiplistMatches(childAbs, e.Name, skiplist)) continue;
                    if (!recursive && depth == 0) continue;
                    await WalkAsync(client, childAbs, childRel, depth + 1).ConfigureAwait(false);
                }
                else
                {
                    // Zero-byte entries in a racing directory are placeholders/status
                    // files, not transferable release data. A real file is queued as
                    // soon as the source listing reports a positive size.
                    if (e.Size <= 0) continue;
                    if (FxpTransfer.IsIncompleteMarker(e.Name)) continue;
                    if (SkiplistMatches(childAbs, e.Name, skiplist)) continue;
                    result.Add(new RaceFile(childAbs, childRel, e.Name, relDir, e.Size));
                }
            }
        }
    }

    // glftpd injects fake "directories" into listings that describe race status rather than
    // real folders, e.g. "[SITE] - ( 6925M 25F - COMPLETE ) - [SITE]", "( 50% de 8F )",
    // "NO-NUKE", "[incomplete] - ...". CWD into these returns 550. Skip them.
    private static bool IsVirtualDir(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        if (n.Contains(" - ( ") || (n.StartsWith("( ") && n.EndsWith(" )"))) return true;
        var lower = n.ToLowerInvariant();
        if (lower.Contains("complete )") || lower.Contains("% ") && lower.Contains("(")) return true;
        return false;
    }

    private async Task EnsureDestDirAsync(FtpClient dst, string destRoot, string relParent, HashSet<string> made, string id)
    {
        // cbftp-style (makeTargetDirectory): CWD to the parent — the daemon resolves any
        // section symlink like /!0day_today. — then create each level with a RELATIVE
        // MKD. An absolute "MKD /!0day_today./Rel" is not resolved by every daemon.
        var segments = new List<string> { destRoot };
        if (!string.IsNullOrEmpty(relParent))
            foreach (var seg in relParent.Split('/', StringSplitOptions.RemoveEmptyEntries))
                segments.Add(seg);
        var path = "";
        foreach (var seg in segments)
        {
            path = path.Length == 0 ? seg : FtpClient.JoinRemote(path, seg);
            if (!made.Add(path)) continue;
            await dst.EnsureCwdAsync(RemoteParentPath(path)).ConfigureAwait(false);
            var (code, _) = await dst.CommandAsync("MKD " + RemoteBase(path)).ConfigureAwait(false);
            // 5xx just means it already exists; that's fine.
        }
    }

    private static int RaceScore(string name)
    {
        var lower = name.ToLowerInvariant();
        if (lower.EndsWith(".sfv")) return 5;
        if (lower.EndsWith(".nfo")) return 4;
        if (lower.EndsWith(".m3u") || lower.EndsWith(".cue")) return 3;
        if (lower.EndsWith(".jpg") || lower.EndsWith(".jpeg") || lower.EndsWith(".png")) return 1; // proof last
        return 2;
    }

    private static bool IsUnreadableSfv(RaceFile file) =>
        file.Size < 8 && file.Name.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase);

    private static int RegisterUploadBusy(
        ConcurrentDictionary<string, (int Count, long Size)> retries, string key, long size)
    {
        var retry = retries.AddOrUpdate(key,
            _ => (1, size),
            (_, previous) => previous.Size != size
                ? (Math.Max(2, previous.Count), size)
                : (Math.Min(previous.Count + 1, 16), size));
        return retry.Count switch
        {
            1 => 100,
            2 => 250,
            <= 5 => 500,
            _ => 1000,
        };
    }

    private static int FastRaceWakeMs(int pollMs) => Math.Clamp(pollMs, 25, 100);

    private static TimeSpan RaceIdleTimeout(int maxIdleCycles, int pollMs)
    {
        var cycles = Math.Clamp(maxIdleCycles, 1, 100000);
        var delay1 = Math.Min(Math.Max(1, pollMs), 30000);
        var delay2 = Math.Min(Math.Max(1, pollMs * 2), 30000);
        var delay4 = Math.Min(Math.Max(1, pollMs * 4), 30000);
        var ms = 0d;
        for (var i = 1; i <= cycles; i++)
            ms += i <= 5 ? delay1 : i <= 15 ? delay2 : delay4;
        return TimeSpan.FromMilliseconds(Math.Clamp(ms, 1000, 30 * 60 * 1000));
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalSeconds < 90
            ? $"{duration.TotalSeconds:0}s"
            : $"{duration.TotalMinutes:0.#}m";

    private static bool AttemptsExceeded(Dictionary<string, Attempt> map, string key)
        => map.TryGetValue(key, out var a) && a.Count >= 7;

    private static bool InBackoff(Dictionary<string, Attempt> map, string key, double nowMs)
    {
        if (!map.TryGetValue(key, out var a)) return false;
        var since = nowMs - a.LastFailMs;
        return (a.Count == 2 && since < 3000) || (a.Count >= 3 && since < 10000);
    }

    private static void RecordFail(Dictionary<string, Attempt> map, string key, double nowMs)
    {
        if (!map.TryGetValue(key, out var a)) { a = new Attempt(); map[key] = a; }
        a.Count++;
        a.LastFailMs = (long)nowMs;
    }

    private static string HumanBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        string[] u = { "KB", "MB", "GB", "TB" };
        double v = bytes; int i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < u.Length - 1);
        return v.ToString("0.#") + " " + u[i];
    }

    private static string FirstLineOf(string message)
    {
        message = (message ?? "").Trim();
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return idx < 0 ? message : message[..idx].Trim();
    }

    private async Task<bool> ReleaseCompleteAfterTransferErrorAsync(
        string id, TransferRequest req, Exception error, JobRunControl run)
    {
        if (string.IsNullOrWhiteSpace(req.ToSite) || string.IsNullOrWhiteSpace(req.DestPath))
            return false;

        try
        {
            var check = await CheckReleaseAsync(req.ToSite, req.DestPath, CancellationToken.None).ConfigureAwait(false);
            if (check.State != ReleaseState.Complete) return false;
            if (!IsCurrentJobRun(id, run)) return false;

            LogJob(id, "warn", $"transfer error ignored because destination is complete: {error.Message}");
            LogJob(id, "info", $"destination complete after transfer error: {req.ToSite}:{req.DestPath} ({check.Description})");
            FinishJob(id, null, run);
            return true;
        }
        catch (Exception checkEx) when (checkEx is not OperationCanceledException)
        {
            if (IsCurrentJobRun(id, run))
                LogJob(id, "warn", $"completion check after transfer error failed: {checkEx.Message}");
            return false;
        }
    }

    // ---- local downloads --------------------------------------------------------------

    public Job StartDownload(DownloadRequest req)
    {
        req.Site = req.Site.Trim();
        req.SourcePath = req.SourcePath.Trim();
        req.DestPath = req.DestPath.Trim();
        if (string.IsNullOrEmpty(req.Site)) throw new ArgumentException("site is required");
        if (string.IsNullOrEmpty(req.SourcePath)) throw new ArgumentException("source_path is required");
        if (_store.Site(req.Site) is null) throw new IOException($"site \"{req.Site}\": not found");

        if (string.IsNullOrEmpty(req.DestPath))
            req.DestPath = Path.Combine(DownloadBase(), RemoteBase(req.SourcePath));
        else if (!Path.IsPathRooted(req.DestPath))
            req.DestPath = Path.Combine(DownloadBase(), req.DestPath);

        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = NewJobId(now),
            Type = JobType.Download,
            State = JobState.Queued,
            Request = new TransferRequest
            {
                FromSite = req.Site,
                ToSite = "local",
                SourcePath = req.SourcePath,
                DestPath = req.DestPath,
                Label = req.Label,
                ViaApi = req.ViaApi,
            },
            CreatedAt = now,
            Events = { new JobEvent { Time = now, Level = "info", Message = "download queued" } },
        };
        var saved = _store.UpsertJob(job);
        Log("transfer", req.Site + " > local", "info", $"queued download {req.SourcePath} -> {req.DestPath}");
        var run = RegisterJobToken(saved.Id);
        ScheduleDownload(saved.Id, req, run);
        return saved;
    }

    private void ScheduleDownload(string id, DownloadRequest req, JobRunControl run)
    {
        var turn = _manualTransferQueue.Enqueue(id, run.Token, LocalTransferLimit(req.Site));
        _ = Task.Run(() => RunQueuedDownloadJobAsync(id, req, run, turn));
    }

    private async Task RunQueuedDownloadJobAsync(string id, DownloadRequest req, JobRunControl run, Task<IDisposable> turn)
    {
        try
        {
            if (!turn.IsCompleted)
                LogJob(id, "info", $"waiting for previous local transfer on {req.Site}");
            using var lease = await turn.ConfigureAwait(false);
            run.Token.ThrowIfCancellationRequested();
            if (_store.Job(id) is not { Terminal: false }) return;
            ArmJobWatchdog(id, run);
            await RunDownloadJobAsync(id, req, run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupCancelledJobRun(id, run);
        }
    }

    private string DownloadBase()
    {
        var dir = _store.Settings().DownloadDir;
        if (string.IsNullOrWhiteSpace(dir)) dir = "downloads";
        if (Path.IsPathRooted(dir)) return dir;
        var exe = Environment.ProcessPath;
        var baseDir = string.IsNullOrEmpty(exe) ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(exe)!;
        return Path.Combine(baseDir, dir);
    }

    private sealed record DlFile(string Remote, string Local, long Size);
    private sealed record LocalTransferSlotPlan(int Slots, int SiteSlots, int LocalSlots, bool LocalAll, int LoginSlots, string Direction);

    private LocalTransferSlotPlan ResolveLocalTransferSlotPlan(Site site, bool download)
    {
        var settings = _store.Settings();
        var configured = download ? site.DownloadSlots : site.UploadSlots;
        var siteLimit = ResolveSiteSlots(configured, site);
        var configuredLocal = download ? settings.LocalDownloadSlots : settings.LocalUploadSlots;
        var localAll = configuredLocal <= 0;
        var localLimit = localAll ? siteLimit : Math.Clamp(configuredLocal, 1, 64);
        var slots = Math.Clamp(Math.Min(siteLimit, localLimit), 1, localLimit);
        var loginLimit = site.LoginSlots > 0 ? site.LoginSlots : 30;
        return new LocalTransferSlotPlan(slots, siteLimit, localLimit, localAll, loginLimit, download ? "download" : "upload");
    }

    private static string SlotCapDetails(Site site, LocalTransferSlotPlan plan)
    {
        var configured = plan.Direction == "download" ? site.DownloadSlots : site.UploadSlots;
        var configuredLabel = configured <= 0 ? "ALL" : configured.ToString();
        var localLabel = plan.LocalAll ? "ALL" : plan.LocalSlots.ToString();
        var login = site.LoginSlots > 0 ? $", login {plan.LoginSlots}" : "";
        return $"site {plan.Direction} {configuredLabel}={plan.SiteSlots}, local {localLabel}{login}";
    }

    private async Task RunDownloadJobAsync(string id, DownloadRequest req, JobRunControl run)
    {
        var ct = run.Token;
        ct.ThrowIfCancellationRequested();
        LogJob(id, "info", "download started");
        _store.UpdateJob(id, j =>
        {
            if (j.Terminal) return;
            j.State = JobState.Running;
            j.StartedAt = DateTime.UtcNow;
        });
        NotifyChanged();
        SitePool? pool = null;
        try
        {
            var site = _store.Site(req.Site) ?? throw new IOException($"site \"{req.Site}\": not found");
            var cfg = FtpConfig(site, "", !req.ViaApi);
            pool = AcquirePool(req.Site, site, cfg);
            var job = _store.Job(id) ?? throw new IOException("job vanished");
            var dest = job.Request.DestPath;
            var settings = _store.Settings();
            var skiplist = MergePatternLists(settings.GlobalSkiplist, site.Skiplist);
            var skipEmptyFolders = settings.SkipEmptyFolders;
            var downloadWasDirectory = false;

            if (SkiplistMatches(req.SourcePath, RemoteBase(req.SourcePath), skiplist))
            {
                LogJob(id, "info", $"skiplist skipped {req.SourcePath}");
                FinishJob(id, null, run);
                return;
            }

            // Phase 1: collect the full file list over one connection.
            var files = new List<DlFile>();
            FtpClient? lister = null;
            try
            {
                lister = await pool.BorrowAsync(ct).ConfigureAwait(false);
                var (code, _) = await lister.CommandAsync("CWD " + req.SourcePath).ConfigureAwait(false);
                if (code / 100 == 2)
                {
                    downloadWasDirectory = true;
                    await CollectDownloadFilesAsync(lister, id, req.SourcePath, dest, 16, skiplist, skipEmptyFolders, files, ct).ConfigureAwait(false);
                }
                else
                    files.Add(new DlFile(req.SourcePath, dest, -1));
            }
            catch
            {
                if (lister is not null)
                {
                    pool.Drop(lister);
                    lister = null;
                }
                throw;
            }
            finally
            {
                if (lister is not null) pool.Return(lister);
            }

            var knownBytes = files.Where(f => f.Size > 0).Sum(f => f.Size);
            _store.UpdateJobTransient(id, j => { j.FilesTotal = files.Count; j.BytesTotal = knownBytes; });

            // Phase 2: drain the list across N parallel connections ("threads"),
            // count from the site's Download slots setting.
            var slotPlan = ResolveLocalTransferSlotPlan(site, download: true);
            var poolCap = pool.TransferCapacity(asSource: true);
            var slotCount = Math.Min(Math.Min(slotPlan.Slots, poolCap), Math.Max(1, files.Count));
            var poolCapDetail = poolCap < slotPlan.Slots ? $", pool transfer {poolCap}" : "";
            LogJob(id, "info", $"downloading {files.Count} file(s) with {slotCount} thread(s) ({SlotCapDetails(site, slotPlan)}{poolCapDetail})");

            var queue = new ConcurrentQueue<DlFile>(files);
            var slotStates = new SlotProgress[slotCount];
            for (var i = 0; i < slotCount; i++) slotStates[i] = new SlotProgress { Slot = i + 1 };
            long doneBytes = 0;
            var filesDone = 0;
            Exception? firstErr = null;
            var downloadedCrcs = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stateLock = new object();
            var lastPush = DateTime.MinValue;
            var liveSfvBanner = downloadWasDirectory &&
                settings.VerifyLocalDownloadsWithSfv &&
                files.Any(f => f.Local.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase));
            var liveBannerLock = new object();
            using var liveBannerSem = new SemaphoreSlim(1, 1);
            var lastLiveBanner = DateTime.MinValue;

            void Push(bool force = false)
            {
                List<SlotProgress> snap;
                long total;
                double speed;
                int fdone;
                lock (stateLock)
                {
                    var now = DateTime.UtcNow;
                    if (!force && (now - lastPush).TotalMilliseconds < 200) return;
                    lastPush = now;
                    snap = slotStates.Where(s => s.File.Length > 0)
                        .Select(s => new SlotProgress { Slot = s.Slot, File = s.File, Done = s.Done, Total = s.Total, Bps = s.Bps })
                        .ToList();
                    total = doneBytes + slotStates.Sum(s => s.Done);
                    speed = slotStates.Sum(s => s.Bps);
                    fdone = filesDone;
                }
                _store.UpdateJobTransient(id, j =>
                {
                    j.Slots = snap;
                    j.BytesDone = total;
                    j.CumulativeBytes = total;
                    j.SpeedBps = speed;
                    j.FilesDone = fdone;
                    j.CurrentFile = snap.Count > 0 ? snap[0].File : j.CurrentFile;
                });
                NotifyChangedThrottled();
            }

            async Task UpdateLiveSfvBannerAsync(bool force = false)
            {
                if (!liveSfvBanner) return;
                lock (liveBannerLock)
                {
                    var now = DateTime.UtcNow;
                    if (!force && (now - lastLiveBanner).TotalSeconds < 2) return;
                    lastLiveBanner = now;
                }
                if (!liveBannerSem.Wait(0)) return;
                try
                {
                    await WriteLocalSfvLiveBannerAsync(dest, files, ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort cosmetic marker. The final SFV verification remains authoritative.
                }
                finally
                {
                    liveBannerSem.Release();
                }
            }

            await UpdateLiveSfvBannerAsync(force: true).ConfigureAwait(false);

            async Task WorkerAsync(int idx)
            {
                var slot = slotStates[idx];
                FtpClient? conn = null;
                var healthy = true;
                try
                {
                    conn = await pool.BorrowTransferAsync(id, asSource: true, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested && queue.TryDequeue(out var f))
                    {
                        await WaitWhilePausedAsync(id, ct).ConfigureAwait(false);
                        var name = RemoteBase(f.Remote);
                        var size = f.Size;
                        if (size <= 0) size = await conn.SizeAsync(f.Remote).ConfigureAwait(false);

                        var dir = Path.GetDirectoryName(f.Local);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                        var localExists = File.Exists(f.Local);
                        var existingSize = localExists ? new FileInfo(f.Local).Length : 0;
                        if (settings.SkipExactSizeLocalFiles && localExists && !File.Exists(f.Local + ".missing") &&
                            size >= 0 && existingSize == size)
                        {
                            lock (stateLock)
                            {
                                doneBytes += size;
                                filesDone++;
                            }
                            LogJob(id, "info", $"[T{idx + 1}] skipped {name}: local file already has {size} bytes");
                            Push(true);
                            await UpdateLiveSfvBannerAsync(force: true).ConfigureAwait(false);
                            continue;
                        }

                        var restartOffset = settings.ResumePartialDownloads && size > 0 && existingSize > 0 && existingSize < size
                            ? existingSize
                            : 0;
                        lock (stateLock) { slot.File = name; slot.Done = restartOffset; slot.Total = Math.Max(0, size); slot.Bps = 0; }
                        LogJob(id, "info", restartOffset > 0
                            ? $"[T{idx + 1}] resuming {f.Remote} at {restartOffset} bytes"
                            : $"[T{idx + 1}] downloading {f.Remote}");

                        var winStart = DateTime.UtcNow;
                        long winBytes = restartOffset;
                        var progress = new SyncProgress<long>(b =>
                        {
                            lock (stateLock)
                            {
                                slot.Done = b;
                                var now = DateTime.UtcNow;
                                var el = (now - winStart).TotalSeconds;
                                if (el > 0.001) slot.Bps = (b - winBytes) / el;
                                if (el > 1.5) { winStart = now; winBytes = b; }
                            }
                            Push();
                        });

                        try
                        {
                            var dlStart = DateTime.UtcNow;
                            long written;
                            var canUseStreamCrc = restartOffset == 0;
                            if (restartOffset > 0)
                            {
                                try
                                {
                                    await using var resumeStream = new FileStream(f.Local, FileMode.Open, FileAccess.Write, FileShare.Read,
                                        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                                    resumeStream.Position = restartOffset;
                                    written = await conn.RetrieveToAsync(f.Remote, resumeStream, ct, progress,
                                        restartOffset: restartOffset).ConfigureAwait(false);
                                }
                                catch (IOException ex) when (ex.Message.StartsWith("REST ", StringComparison.OrdinalIgnoreCase))
                                {
                                    LogJob(id, "warn", $"[T{idx + 1}] server cannot resume {name}; restarting the file");
                                    restartOffset = 0;
                                    canUseStreamCrc = true;
                                    lock (stateLock) slot.Done = 0;
                                    await using var restartStream = File.Create(f.Local);
                                    using var restartCrcStream = new LocalCrc32WriteStream(restartStream);
                                    written = await conn.RetrieveToAsync(f.Remote, restartCrcStream, ct, progress).ConfigureAwait(false);
                                    downloadedCrcs[Path.GetFullPath(f.Local)] = restartCrcStream.Hex;
                                }
                            }
                            else
                            {
                                await using var fileStream = File.Create(f.Local);
                                using var crcStream = new LocalCrc32WriteStream(fileStream);
                                written = await conn.RetrieveToAsync(f.Remote, crcStream, ct, progress).ConfigureAwait(false);
                                downloadedCrcs[Path.GetFullPath(f.Local)] = crcStream.Hex;
                            }
                            var finalSize = restartOffset + written;
                            if (size >= 0 && finalSize != size)
                                throw new IOException($"downloaded size mismatch for {name}: got {finalSize}, expected {size}");
                            if (!canUseStreamCrc)
                                downloadedCrcs.TryRemove(Path.GetFullPath(f.Local), out _);
                            lock (stateLock)
                            {
                                doneBytes += finalSize;
                                filesDone++;
                                slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0;
                            }
                            _store.AddSiteTraffic(req.Site, written, 0, (DateTime.UtcNow - dlStart).TotalSeconds);
                            LogJob(id, "info", $"[T{idx + 1}] downloaded {name} ({written} bytes)");
                            Push(true);
                            await UpdateLiveSfvBannerAsync(force: true).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; firstErr ??= ex; }
                            LogJob(id, "error", $"[T{idx + 1}] {name}: {FirstLineOf(ex.Message)}");
                            // The connection may be broken — replace it and keep going.
                            pool.DropTransfer(id, asSource: true, conn);
                            conn = null;
                            conn = await pool.BorrowTransferAsync(id, asSource: true, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    healthy = false;
                    throw;
                }
                finally
                {
                    if (conn is not null)
                    {
                        if (healthy) pool.ReturnTransfer(id, asSource: true, conn);
                        else pool.DropTransfer(id, asSource: true, conn);
                    }
                    lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, slotCount).Select(WorkerAsync)).ConfigureAwait(false);
            Push(true);
            await UpdateLiveSfvBannerAsync(force: true).ConfigureAwait(false);
            _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
            if (firstErr is not null)
                throw new IOException($"download finished with errors: {firstErr.Message}", firstErr);
            if (downloadWasDirectory && settings.VerifyLocalDownloadsWithSfv)
                await VerifyLocalDownloadAsync(id, dest, files, downloadedCrcs, ct).ConfigureAwait(false);
            FinishJob(id, null, run);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user — CancelJobInternal already marked the job.
            CleanupCancelledJobRun(id, run);
        }
        catch (Exception ex)
        {
            FinishJob(id, ex, run);
        }
        finally
        {
            if (pool is not null) ReleasePool(req.Site);
        }
    }

    private async Task CollectDownloadFilesAsync(FtpClient client, string id, string remotePath, string localPath,
        int depth, List<string> skiplist, bool skipEmptyFolders, List<DlFile> files, CancellationToken ct)
    {
        if (depth <= 0) throw new IOException($"maximum directory depth reached at {remotePath}");
        ct.ThrowIfCancellationRequested();
        var entries = await client.ListAsync(remotePath, ct).ConfigureAwait(false);
        var transferable = new List<RemoteEntry>();
        foreach (var entry in entries)
        {
            if (entry.Name is "." or "..") continue;
            var childRemote = FtpClient.JoinRemote(remotePath, entry.Name);
            // Skip glftpd's 0-byte "-missing" placeholders automatically.
            if (entry.Type is not ("dir" or "link") && FxpTransfer.IsIncompleteMarker(entry.Name))
            {
                LogJob(id, "info", $"skipped incomplete marker {entry.Name}");
                continue;
            }
            if (SkiplistMatches(childRemote, entry.Name, skiplist))
            {
                LogJob(id, "info", $"skiplist skipped {childRemote}");
                continue;
            }
            transferable.Add(entry);
        }

        if (skipEmptyFolders && transferable.Count == 0)
        {
            LogJob(id, "info", $"skipped empty directory {remotePath}");
            return;
        }

        Directory.CreateDirectory(localPath);
        foreach (var entry in transferable)
        {
            var childRemote = FtpClient.JoinRemote(remotePath, entry.Name);
            var childLocal = Path.Combine(localPath, Path.GetFileName(entry.Name));
            if (entry.Type is "dir" or "link")
            {
                if (IsVirtualDir(entry.Name)) continue; // glftpd tag/status dirs
                try
                {
                    await CollectDownloadFilesAsync(client, id, childRemote, childLocal, depth - 1, skiplist, skipEmptyFolders, files, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsIgnorableDirectoryMiss(ex))
                {
                    LogJob(id, "warn", $"skipped empty/virtual directory {childRemote}: {FirstLineOf(ex.Message)}");
                }
            }
            else
            {
                files.Add(new DlFile(childRemote, childLocal, entry.Size));
            }
        }
    }

    private sealed record LocalSfvIssue(string Name, string Path, string Reason);
    private sealed record LocalSfvExpected(string Name, string Path, long Size);

    private async Task WriteLocalSfvLiveBannerAsync(string destination, List<DlFile> files, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var sfvPaths = files
            .Where(f => f.Local.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && File.Exists(f.Local))
            .Select(f => Path.GetFullPath(f.Local))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        RemoveLocalVerificationMarkers(root);
        if (sfvPaths.Count == 0)
        {
            CreateLocalVerificationMarker(root, "[WFXP] - ( 0% of 0F - WAITING FOR SFV ) - [WFXP]");
            return;
        }

        var expected = new Dictionary<string, LocalSfvExpected>(StringComparer.OrdinalIgnoreCase);
        var knownSizes = files
            .Where(f => f.Size > 0)
            .GroupBy(f => Path.GetFullPath(f.Local), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Size, StringComparer.OrdinalIgnoreCase);

        foreach (var sfvPath in sfvPaths)
        {
            ct.ThrowIfCancellationRequested();
            var entries = Sfv.Parse(await File.ReadAllTextAsync(sfvPath, ct).ConfigureAwait(false));
            var sfvDir = Path.GetDirectoryName(sfvPath) ?? root;
            foreach (var entry in entries)
            {
                var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var localPath = Path.GetFullPath(Path.Combine(sfvDir, relative));
                if (!localPath.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    !localPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                knownSizes.TryGetValue(localPath, out var size);
                expected[localPath] = new LocalSfvExpected(entry.Name, localPath, size);
            }
        }

        if (expected.Count == 0)
        {
            CreateLocalVerificationMarker(root, "[WFXP] - ( 0% of 0F - SFV UNREADABLE ) - [WFXP]");
            return;
        }

        var complete = 0;
        long bytesDone = 0;
        long bytesTotal = 0;
        foreach (var item in expected.Values)
        {
            ct.ThrowIfCancellationRequested();
            var size = item.Size;
            var exists = File.Exists(item.Path);
            var length = exists ? new FileInfo(item.Path).Length : 0;
            if (size > 0)
            {
                bytesTotal += size;
                bytesDone += Math.Clamp(length, 0, size);
                if (exists && length >= size) complete++;
            }
            else if (exists && length > 0)
            {
                complete++;
            }
        }

        var percent = bytesTotal > 0
            ? (int)Math.Clamp(bytesDone * 100 / bytesTotal, 0, 100)
            : (int)Math.Clamp((long)complete * 100 / expected.Count, 0, 100);
        var missing = Math.Max(0, expected.Count - complete);
        var status = missing == 0 ? "VERIFYING" : $"INCOMPLETE {missing}F";
        CreateLocalVerificationMarker(root, $"[WFXP] - ( {percent}% of {expected.Count}F - {status} ) - [WFXP]");
    }

    private async Task VerifyLocalDownloadAsync(string id, string destination, List<DlFile> files,
        IReadOnlyDictionary<string, string> downloadedCrcs, CancellationToken ct)
    {
        var root = Path.GetFullPath(destination);
        var sfvPaths = files
            .Where(f => f.Local.EndsWith(".sfv", StringComparison.OrdinalIgnoreCase) && File.Exists(f.Local))
            .Select(f => Path.GetFullPath(f.Local))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (sfvPaths.Count == 0)
        {
            LogJob(id, "info", "local SFV verification skipped: no SFV was downloaded");
            return;
        }

        LogJob(id, "info", $"verifying {sfvPaths.Count} local SFV file(s)");
        var expected = new Dictionary<string, (string Name, string Crc)>(StringComparer.OrdinalIgnoreCase);
        var issues = new List<LocalSfvIssue>();
        long verifiedBytes = 0;
        long crcReadBytes = 0;
        var lastVerifyPush = DateTime.MinValue;
        var rootPrefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

        foreach (var sfvPath in sfvPaths)
        {
            ct.ThrowIfCancellationRequested();
            var entries = Sfv.Parse(await File.ReadAllTextAsync(sfvPath, ct).ConfigureAwait(false));
            if (entries.Count == 0)
            {
                issues.Add(new LocalSfvIssue(Path.GetFileName(sfvPath), sfvPath, "SFV contains no valid entries"));
                continue;
            }

            var sfvDir = Path.GetDirectoryName(sfvPath) ?? root;
            foreach (var entry in entries)
            {
                var relative = entry.Name.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var localPath = Path.GetFullPath(Path.Combine(sfvDir, relative));
                if (!localPath.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    !localPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    issues.Add(new LocalSfvIssue(entry.Name, localPath, "SFV path escapes the download folder"));
                    continue;
                }
                expected[localPath] = (entry.Name, entry.Crc.Trim());
            }
        }

        if (downloadedCrcs.Count > 0)
        {
            var usable = expected.Keys.Count(path => downloadedCrcs.ContainsKey(path));
            if (usable > 0)
                LogJob(id, "info", $"local SFV verify using live CRC for {usable}/{expected.Count} file(s)");
        }

        void PushVerifyProgress(string file, bool force = false)
        {
            var now = DateTime.UtcNow;
            if (!force && (now - lastVerifyPush).TotalMilliseconds < 500) return;
            lastVerifyPush = now;
            _store.UpdateJobTransient(id, j =>
            {
                j.CurrentFile = file;
                j.SpeedBps = 0;
            });
            NotifyChangedThrottled();
        }

        foreach (var item in expected)
        {
            ct.ThrowIfCancellationRequested();
            var marker = item.Key + ".missing";
            if (!File.Exists(item.Key))
            {
                issues.Add(new LocalSfvIssue(item.Value.Name, item.Key, "missing"));
                CreateMissingMarker(marker);
                continue;
            }

            PushVerifyProgress("verifying " + item.Value.Name);
            var actual = downloadedCrcs.TryGetValue(item.Key, out var liveCrc)
                ? liveCrc
                : await LocalCrc32Async(item.Key, bytes =>
                {
                    var done = Interlocked.Add(ref crcReadBytes, bytes);
                    var name = $"{item.Value.Name} ({FormatBytes(done)} checked)";
                    PushVerifyProgress("verifying " + name);
                }, ct).ConfigureAwait(false);
            var wanted = item.Value.Crc.Trim().TrimStart('0', 'x', 'X').PadLeft(8, '0');
            if (wanted.Length != 8 || !actual.Equals(wanted, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new LocalSfvIssue(item.Value.Name, item.Key, $"bad CRC: {actual}, expected {item.Value.Crc}"));
                CreateMissingMarker(marker);
            }
            else
            {
                verifiedBytes += new FileInfo(item.Key).Length;
                TryDeleteFile(marker);
            }
        }
        PushVerifyProgress("", force: true);

        RemoveLocalVerificationMarkers(root);
        if (issues.Count == 0)
        {
            var verifiedMegabytes = (long)Math.Round(
                verifiedBytes / (1024d * 1024d),
                MidpointRounding.AwayFromZero);
            var markerName = $"[WFXP] - ( {verifiedMegabytes}M {expected.Count}F - COMPLETE ) - [WFXP]";
            CreateLocalVerificationMarker(root, markerName);
            LogJob(id, "info", $"local SFV verified: {expected.Count}/{expected.Count} files correct; created {markerName}");
            return;
        }

        var incompleteName = $"INCOMPLETE - {issues.Count} MISSING";
        CreateLocalVerificationMarker(root, incompleteName);
        foreach (var issue in issues.Take(100))
            LogJob(id, "error", $"SFV {issue.Name}: {issue.Reason}");
        throw new IOException($"local SFV verification failed: {issues.Count} missing or CRC-bad file(s)");
    }

    private static void CreateLocalVerificationMarker(string root, string name)
    {
        Directory.CreateDirectory(Path.Combine(root, name));
    }

    private static void CreateMissingMarker(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir)) Directory.CreateDirectory(dir);
            using var _ = File.Create(path);
        }
        catch { /* the verification result still fails even if a marker cannot be created */ }
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { }
    }

    private static void RemoveLocalVerificationMarkers(string root)
    {
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            var wfxpMarker = name.StartsWith("[WFXP] - ( ", StringComparison.OrdinalIgnoreCase) &&
                (name.EndsWith(" ) - [WFXP]", StringComparison.OrdinalIgnoreCase) ||
                 name.EndsWith(" ) - [WFX]", StringComparison.OrdinalIgnoreCase));
            if (!name.StartsWith("COMPLETE - ", StringComparison.OrdinalIgnoreCase) &&
                !wfxpMarker &&
                !name.StartsWith("INCOMPLETE - ", StringComparison.OrdinalIgnoreCase))
                continue;
            try { Directory.Delete(path, recursive: false); }
            catch { }
        }
    }

    private static readonly uint[] LocalCrc32Table = BuildLocalCrc32Table();

    private static uint[] BuildLocalCrc32Table()
    {
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? 0xedb88320u ^ (crc >> 1) : crc >> 1;
            table[i] = crc;
        }
        return table;
    }

    private static async Task<string> LocalCrc32Async(string path, Action<int>? progress, CancellationToken ct)
    {
        uint crc = 0xffffffffu;
        var buffer = new byte[1024 * 1024];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            buffer.Length, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) break;
            crc = UpdateLocalCrc32(crc, buffer.AsSpan(0, read));
            progress?.Invoke(read);
        }
        return (~crc).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static uint UpdateLocalCrc32(uint crc, ReadOnlySpan<byte> buffer)
    {
        for (var i = 0; i < buffer.Length; i++)
            crc = LocalCrc32Table[(crc ^ buffer[i]) & 0xff] ^ (crc >> 8);
        return crc;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.0} {units[unit]}";
    }

    private sealed class LocalCrc32WriteStream : Stream
    {
        private readonly Stream _inner;
        private uint _crc = 0xffffffffu;

        public LocalCrc32WriteStream(Stream inner) => _inner = inner;

        public string Hex => (~_crc).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => _inner.SetLength(value);

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            _crc = UpdateLocalCrc32(_crc, buffer.AsSpan(offset, count));
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            _inner.Write(buffer);
            _crc = UpdateLocalCrc32(_crc, buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            _crc = UpdateLocalCrc32(_crc, buffer.Span);
        }
    }

    // ---- local uploads ----------------------------------------------------------------

    public Job StartUpload(UploadRequest req)
    {
        req.Site = req.Site.Trim();
        req.SourcePath = req.SourcePath.Trim();
        req.DestPath = req.DestPath.Trim();
        if (string.IsNullOrEmpty(req.Site)) throw new ArgumentException("site is required");
        if (string.IsNullOrEmpty(req.SourcePath)) throw new ArgumentException("source_path is required");
        if (!Path.IsPathRooted(req.SourcePath)) req.SourcePath = Path.GetFullPath(req.SourcePath);
        if (_store.Site(req.Site) is null) throw new IOException($"site \"{req.Site}\": not found");
        if (string.IsNullOrEmpty(req.DestPath)) req.DestPath = "/" + Path.GetFileName(req.SourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        var now = DateTime.UtcNow;
        var job = new Job
        {
            Id = NewJobId(now),
            Type = JobType.Upload,
            State = JobState.Queued,
            Request = new TransferRequest
            {
                FromSite = "local",
                ToSite = req.Site,
                SourcePath = req.SourcePath,
                DestPath = req.DestPath,
                Label = req.Label,
                ViaApi = req.ViaApi,
            },
            CreatedAt = now,
            Events = { new JobEvent { Time = now, Level = "info", Message = "upload queued" } },
        };
        var saved = _store.UpsertJob(job);
        Log("transfer", "local > " + req.Site, "info", $"queued upload {req.SourcePath} -> {req.DestPath}");
        var run = RegisterJobToken(saved.Id);
        ScheduleUpload(saved.Id, req, run);
        return saved;
    }

    private void ScheduleUpload(string id, UploadRequest req, JobRunControl run)
    {
        var turn = _manualTransferQueue.Enqueue(id, run.Token, LocalTransferLimit(req.Site));
        _ = Task.Run(() => RunQueuedUploadJobAsync(id, req, run, turn));
    }

    private async Task RunQueuedUploadJobAsync(string id, UploadRequest req, JobRunControl run, Task<IDisposable> turn)
    {
        try
        {
            if (!turn.IsCompleted)
                LogJob(id, "info", $"waiting for previous local transfer on {req.Site}");
            using var lease = await turn.ConfigureAwait(false);
            run.Token.ThrowIfCancellationRequested();
            if (_store.Job(id) is not { Terminal: false }) return;
            ArmJobWatchdog(id, run);
            await RunUploadJobAsync(id, req, run).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CleanupCancelledJobRun(id, run);
        }
    }

    private sealed record UlFile(string Local, string Remote, long Size);

    private async Task RunUploadJobAsync(string id, UploadRequest req, JobRunControl run)
    {
        var ct = run.Token;
        ct.ThrowIfCancellationRequested();
        LogJob(id, "info", "upload started");
        _store.UpdateJob(id, j =>
        {
            if (j.Terminal) return;
            j.State = JobState.Running;
            j.StartedAt = DateTime.UtcNow;
        });
        NotifyChanged();
        SitePool? pool = null;
        try
        {
            var site = _store.Site(req.Site) ?? throw new IOException($"site \"{req.Site}\": not found");
            var cfg = FtpConfig(site, "", !req.ViaApi);
            pool = AcquirePool(req.Site, site, cfg);
            var job = _store.Job(id) ?? throw new IOException("job vanished");
            var settings = _store.Settings();
            var files = CollectUploadFiles(req.SourcePath, job.Request.DestPath);
            var knownBytes = files.Where(f => f.Size > 0).Sum(f => f.Size);
            _store.UpdateJobTransient(id, j => { j.FilesTotal = files.Count; j.BytesTotal = knownBytes; });

            var slotPlan = ResolveLocalTransferSlotPlan(site, download: false);
            var poolCap = pool.TransferCapacity(asSource: false);
            var slotCount = Math.Min(Math.Min(slotPlan.Slots, poolCap), Math.Max(1, files.Count));
            var poolCapDetail = poolCap < slotPlan.Slots ? $", pool transfer {poolCap}" : "";
            LogJob(id, "info", $"uploading {files.Count} file(s) with {slotCount} thread(s) ({SlotCapDetails(site, slotPlan)}{poolCapDetail})");

            var queue = new ConcurrentQueue<UlFile>(files);
            var slotStates = new SlotProgress[slotCount];
            for (var i = 0; i < slotCount; i++) slotStates[i] = new SlotProgress { Slot = i + 1 };
            long doneBytes = 0;
            var filesDone = 0;
            Exception? firstErr = null;
            var stateLock = new object();
            var lastPush = DateTime.MinValue;
            var madeDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var dirSem = new SemaphoreSlim(1, 1);

            void Push(bool force = false)
            {
                List<SlotProgress> snap;
                long total;
                double speed;
                int fdone;
                lock (stateLock)
                {
                    var now = DateTime.UtcNow;
                    if (!force && (now - lastPush).TotalMilliseconds < 200) return;
                    lastPush = now;
                    snap = slotStates.Where(s => s.File.Length > 0)
                        .Select(s => new SlotProgress { Slot = s.Slot, File = s.File, Done = s.Done, Total = s.Total, Bps = s.Bps })
                        .ToList();
                    total = doneBytes + slotStates.Sum(s => s.Done);
                    speed = slotStates.Sum(s => s.Bps);
                    fdone = filesDone;
                }
                _store.UpdateJobTransient(id, j =>
                {
                    j.Slots = snap;
                    j.BytesDone = total;
                    j.CumulativeBytes = total;
                    j.SpeedBps = speed;
                    j.FilesDone = fdone;
                    j.CurrentFile = snap.Count > 0 ? snap[0].File : j.CurrentFile;
                });
                NotifyChanged();
            }

            async Task WorkerAsync(int idx)
            {
                var slot = slotStates[idx];
                FtpClient? conn = null;
                var healthy = true;
                try
                {
                    conn = await pool.BorrowTransferAsync(id, asSource: false, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested && queue.TryDequeue(out var f))
                    {
                        await WaitWhilePausedAsync(id, ct).ConfigureAwait(false);
                        var name = Path.GetFileName(f.Local);
                        if (settings.SkipExactSizeLocalFiles)
                        {
                            var remoteSize = await conn.SizeAsync(f.Remote).ConfigureAwait(false);
                            if (remoteSize >= 0 && remoteSize == f.Size)
                            {
                                lock (stateLock)
                                {
                                    doneBytes += f.Size;
                                    filesDone++;
                                }
                                LogJob(id, "info", $"[T{idx + 1}] skipped {name}: remote file already has {f.Size} bytes");
                                Push(true);
                                continue;
                            }
                        }
                        lock (stateLock) { slot.File = name; slot.Done = 0; slot.Total = Math.Max(0, f.Size); slot.Bps = 0; }
                        LogJob(id, "info", $"[T{idx + 1}] uploading {f.Local}");

                        await dirSem.WaitAsync(ct).ConfigureAwait(false);
                        try { await EnsureRemoteDirRecursiveAsync(conn, RemoteParentPath(f.Remote), madeDirs).ConfigureAwait(false); }
                        finally { dirSem.Release(); }

                        var winStart = DateTime.UtcNow;
                        long winBytes = 0;
                        var progress = new SyncProgress<long>(b =>
                        {
                            lock (stateLock)
                            {
                                slot.Done = b;
                                var now = DateTime.UtcNow;
                                var el = (now - winStart).TotalSeconds;
                                if (el > 0.001) slot.Bps = (b - winBytes) / el;
                                if (el > 1.5) { winStart = now; winBytes = b; }
                            }
                            Push();
                        });

                        try
                        {
                            var ulStart = DateTime.UtcNow;
                            await using var fileStream = File.OpenRead(f.Local);
                            var written = await conn.StoreFromAsync(f.Remote, fileStream, ct, progress).ConfigureAwait(false);
                            lock (stateLock)
                            {
                                doneBytes += written;
                                filesDone++;
                                slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0;
                            }
                            _store.AddSiteTraffic(req.Site, 0, written, (DateTime.UtcNow - ulStart).TotalSeconds);
                            LogJob(id, "info", $"[T{idx + 1}] uploaded {name} ({written} bytes)");
                            Push(true);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; firstErr ??= ex; }
                            LogJob(id, "error", $"[T{idx + 1}] {name}: {FirstLineOf(ex.Message)}");
                            pool.DropTransfer(id, asSource: false, conn);
                            conn = null;
                            conn = await pool.BorrowTransferAsync(id, asSource: false, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    healthy = false;
                    throw;
                }
                finally
                {
                    if (conn is not null)
                    {
                        if (healthy) pool.ReturnTransfer(id, asSource: false, conn);
                        else pool.DropTransfer(id, asSource: false, conn);
                    }
                    lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, slotCount).Select(WorkerAsync)).ConfigureAwait(false);
            Push(true);
            _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
            if (firstErr is not null)
                throw new IOException($"upload finished with errors: {firstErr.Message}", firstErr);
            FinishJob(id, null, run);
        }
        catch (OperationCanceledException)
        {
            CleanupCancelledJobRun(id, run);
        }
        catch (Exception ex)
        {
            FinishJob(id, ex, run);
        }
        finally
        {
            if (pool is not null) ReleasePool(req.Site);
        }
    }

    private static List<UlFile> CollectUploadFiles(string sourcePath, string destPath)
    {
        sourcePath = Path.GetFullPath(sourcePath);
        destPath = NormalizeRemoteForUpload(destPath);
        var files = new List<UlFile>();
        if (File.Exists(sourcePath))
        {
            files.Add(new UlFile(sourcePath, destPath, new FileInfo(sourcePath).Length));
            return files;
        }
        if (!Directory.Exists(sourcePath)) throw new IOException($"local path \"{sourcePath}\": not found");

        foreach (var file in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            if (file.EndsWith(".missing", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = Path.GetRelativePath(sourcePath, file).Replace('\\', '/');
            var remote = FtpClient.JoinRemote(destPath, rel);
            files.Add(new UlFile(file, remote, new FileInfo(file).Length));
        }
        return files;
    }

    private static string NormalizeRemoteForUpload(string path)
    {
        path = (path ?? "/").Replace('\\', '/').Trim();
        if (path.Length == 0) return "/";
        if (!path.StartsWith('/')) path = "/" + path;
        while (path.Contains("//", StringComparison.Ordinal)) path = path.Replace("//", "/");
        return path.Length > 1 ? path.TrimEnd('/') : path;
    }

    private static string RemoteParentPath(string path)
    {
        path = NormalizeRemoteForUpload(path).TrimEnd('/');
        if (path.Length == 0 || path == "/") return "/";
        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    private static async Task EnsureRemoteDirRecursiveAsync(FtpClient client, string dir, HashSet<string> made)
    {
        dir = NormalizeRemoteForUpload(dir);
        if (dir == "/") return;

        var current = "";
        foreach (var segment in dir.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current.Length == 0 ? "/" + segment : FtpClient.JoinRemote(current, segment);
            if (!made.Add(current)) continue;
            await client.CommandAsync("MKD " + current).ConfigureAwait(false);
        }
    }

    // ---- job control (stop / pause) ----------------------------------------------------

    // Stop: per-job CancellationTokenSource — cancelling actually aborts the running
    // transfer work, not just the job row. Pause: workers finish the file in flight,
    // then hold before picking up the next one.
    private sealed class JobRunControl : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        public CancellationToken Token => _cts.Token;
        public void Cancel() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }
        public void Dispose() => _cts.Dispose();
    }

    private readonly ConcurrentDictionary<string, JobRunControl> _jobRuns = new();
    private readonly ConcurrentDictionary<string, bool> _jobPaused = new();
    private readonly ManualTransferQueue _manualTransferQueue = new();
    private readonly object _jobRunLock = new();

    private JobRunControl RegisterJobToken(string id)
    {
        var run = new JobRunControl();
        lock (_jobRunLock)
        {
            if (_jobRuns.TryGetValue(id, out var previous)) previous.Cancel();
            _jobRuns[id] = run;
        }
        return run;
    }

    private bool IsCurrentJobRun(string id, JobRunControl run)
    {
        lock (_jobRunLock)
            return _jobRuns.TryGetValue(id, out var current) && ReferenceEquals(current, run);
    }

    private void ArmJobWatchdog(string id, JobRunControl run)
    {
        var timeout = TimeSpan.FromMinutes(Math.Max(5, _store.Settings().JobWatchdogTimeoutMinutes));
        _ = Task.Run(async () =>
        {
            try
            {
                var interval = TimeSpan.FromSeconds(Math.Clamp(timeout.TotalSeconds / 4, 15, 60));
                while (true)
                {
                    await Task.Delay(interval).ConfigureAwait(false);
                    Job? failed = null;
                    string? reason = null;
                    lock (_jobRunLock)
                    {
                        if (!IsCurrentJobRun(id, run)) return;
                        var job = _store.Job(id);
                        if (job is null || job.Terminal) return;
                        if (job.Paused) continue;

                        var lastActivity = job.HeartbeatAt != default
                            ? job.HeartbeatAt
                            : job.StartedAt != default ? job.StartedAt : job.CreatedAt;
                        if (lastActivity == default || DateTime.UtcNow - lastActivity < timeout) continue;

                        reason = $"job watchdog timeout after {timeout.TotalMinutes:0} minute(s) without activity";
                        run.Cancel();
                        failed = _store.FailJobIfStillRunning(id, reason);
                    }
                    if (failed is not null)
                    {
                        Log("transfer", TransferRoute(failed.Request), "error", reason);
                        NotifyChanged();
                    }
                    return;
                }
            }
            catch { }
        });
    }

    private void UnregisterJobToken(string id, JobRunControl run)
    {
        lock (_jobRunLock)
        {
            var removed = ((ICollection<KeyValuePair<string, JobRunControl>>)_jobRuns)
                .Remove(new KeyValuePair<string, JobRunControl>(id, run));
            if (removed) _jobPaused.TryRemove(id, out _);
        }
        try { run.Dispose(); } catch { }
    }

    private void CleanupCancelledJobRun(string id, JobRunControl run)
    {
        var changed = false;
        lock (_jobRunLock)
        {
            if (IsCurrentJobRun(id, run))
            {
                _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
                ClearProgress(id);
                changed = true;
            }
            UnregisterJobToken(id, run);
        }
        if (changed) NotifyChanged();
    }

    public bool PauseJob(string id)
    {
        var job = _store.Job(id);
        if (job is null || job.Terminal) return false;
        _jobPaused[id] = true;
        _meshScoreboard.SetPaused(id, true);
        lock (_poolLock)
            foreach (var pool in _pools.Values) pool.SetMeshDemand(id, false, false);
        _store.UpdateJob(id, j => j.Paused = true);
        LogJob(id, "info", "job paused (finishes the file in flight, then waits)");
        return true;
    }

    public bool ResumeJob(string id)
    {
        _jobPaused.TryRemove(id, out _);
        _meshScoreboard.SetPaused(id, false);
        var job = _store.UpdateJob(id, j => j.Paused = false);
        if (job is null) return false;
        LogJob(id, "info", "job resumed");
        return true;
    }

    private bool IsJobPaused(string id) => _jobPaused.ContainsKey(id);

    private static ManualTransferQueue.Limit LocalTransferLimit(string site) => new("local:" + site.Trim(), 1);

    private Task<bool> ScheduleTransfer(string id, TransferRequest req, JobRunControl run, ManualTransferQueue.Limit? batch = null)
    {
        if (req.Race)
        {
            ArmJobWatchdog(id, run);
            return Task.Run(() => RunTransferJobAsync(id, req, run.Token, run));
        }
        var limit = new ManualTransferQueue.Limit("fxp", _store.Settings().MaxConcurrentFxpJobs);
        var turn = _manualTransferQueue.Enqueue(id, run.Token, batch is null ? new[] { limit } : new[] { limit, batch });
        return Task.Run(async () =>
        {
            try
            {
                using var lease = await turn.ConfigureAwait(false);
                run.Token.ThrowIfCancellationRequested();
                if (_store.Job(id) is not { Terminal: false }) return false;
                await WaitWhilePausedAsync(id, run.Token).ConfigureAwait(false);
                ArmJobWatchdog(id, run);
                return await RunTransferJobAsync(id, req, run.Token, run).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                CleanupCancelledJobRun(id, run);
                return false;
            }
        });
    }

    // Block while paused; throws when the job is stopped mid-pause.
    private async Task WaitWhilePausedAsync(string id, CancellationToken ct)
    {
        while (IsJobPaused(id))
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(300, ct).ConfigureAwait(false);
        }
    }

    // ---- job bookkeeping --------------------------------------------------------------

    private void LogJob(string id, string level, string message)
    {
        var job = _store.UpdateJob(id, j => AppendJobEvent(j, level, message));
        var route = job is null ? id : TransferRoute(job.Request);
        Log("transfer", route, level, message);
        NotifyChanged();
    }

    // Hot-path job log: appends the event in memory only — no state.json disk write
    // per line (LogJob persists on every call, which throttles a busy race). The next
    // persisting UpdateJob (e.g. FinishJob) flushes everything accumulated.
    private void LogJobLive(string id, string level, string message)
    {
        var job = _store.UpdateJobTransient(id, j => AppendJobEvent(j, level, message));
        var route = job is null ? id : TransferRoute(job.Request);
        Log("transfer", route, level, message); // Log() already throttles UI notify
    }

    private static void AppendJobEvent(Job job, string level, string message)
    {
        job.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = level, Message = message });
        if (job.Events.Count > MaxJobEvents)
            job.Events.RemoveRange(0, job.Events.Count - MaxJobEvents);
    }

    private void FinishJob(string id, Exception? error, JobRunControl run)
    {
        Job? job;
        lock (_jobRunLock)
        {
            if (!IsCurrentJobRun(id, run))
            {
                UnregisterJobToken(id, run);
                return;
            }
            job = _store.UpdateJob(id, j =>
            {
                if (j.Terminal) return; // already cancelled/stopped — don't overwrite
                j.FinishedAt = DateTime.UtcNow;
                j.Paused = false;
                j.Slots = new List<SlotProgress>();
                if (error is not null)
                {
                    j.State = JobState.Failed;
                    j.Error = error.Message;
                    j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "error", Message = error.Message });
                }
                else
                {
                    j.State = JobState.Succeeded;
                    j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "info", Message = "job completed" });
                }
                if (error is null)
                {
                    // The race stops the instant the release is complete, so some per-file rows
                    // can be frozen mid-flight ("queued"/"wait" = pending/source uploading,
                    // "active" = in progress). The release IS complete, so those files ended
                    // up on the dest — flip leftovers to a terminal status instead of showing
                    // stale live rows.
                    foreach (var row in j.Files)
                    {
                        if (row.Status is "queued" or "wait" or "active")
                        {
                            row.Status = "dupe";
                            if (string.IsNullOrEmpty(row.Error))
                                row.Error = "already present when the race finished";
                        }
                    }
                }
            });
            ClearProgress(id);
            UnregisterJobToken(id, run);
        }
        if (job is not null)
        {
            var route = TransferRoute(job.Request);
            if (error is not null) Log("transfer", route, "error", "job failed: " + error.Message);
            else Log("transfer", route, "info", $"job {job.Id} finished: {job.State.ToString().ToLowerInvariant()}");
        }
        NotifyChanged();
    }

    private void CancelJobInternal(string id, string reason, JobRunControl? expectedRun = null)
    {
        Job? job;
        lock (_jobRunLock)
        {
            if (expectedRun is not null && !IsCurrentJobRun(id, expectedRun)) return;
            // Actually abort the running work, not just flip the row's state.
            if (expectedRun is not null) expectedRun.Cancel();
            else if (_jobRuns.TryGetValue(id, out var currentRun)) currentRun.Cancel();
            _jobPaused.TryRemove(id, out _);
            job = _store.UpdateJob(id, j =>
            {
                if (j.Terminal) return;
                j.State = JobState.Cancelled;
                j.FinishedAt = DateTime.UtcNow;
                j.Paused = false;
                j.Slots = new List<SlotProgress>();
                j.Error = reason;
                j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "warn", Message = reason });
            });
        }
        if (job is not null)
        {
            var route = TransferRoute(job.Request);
            Log("transfer", route, "warn", $"job {job.Id} cancelled: {reason}");
        }
        NotifyChanged();
    }

    // ---- interfaces -------------------------------------------------------------------

    public List<NetworkInterfaceInfo> Interfaces()
    {
        var result = new List<NetworkInterfaceInfo>();
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                var ip = addr.Address;
                if (ip.IsIPv6LinkLocal) continue;
                result.Add(new NetworkInterfaceInfo
                {
                    Name = iface.Name,
                    Address = ip.ToString(),
                    Value = iface.Name + ", " + ip,
                    Loopback = IPAddress.IsLoopback(ip),
                    IPv6 = ip.AddressFamily == AddressFamily.InterNetworkV6,
                });
            }
        }
        return result;
    }

    // ---- helpers ----------------------------------------------------------------------

    private static int _idCounter;
    private static string NextSeq() => (Interlocked.Increment(ref _idCounter) & 0xFFFFFF).ToString("D6");
    private static string NewJobId(DateTime t) => $"job-{t:yyyyMMdd-HHmmss}-{NextSeq()}";
    private static string NewBatchId(DateTime t) => $"batch-{t:yyyyMMdd-HHmmss}-{NextSeq()}";

    private static string RemoteBase(string path)
    {
        path = (path ?? "").Trim().TrimEnd('/');
        if (path.Length == 0) return "";
        var i = path.LastIndexOf('/');
        return i >= 0 ? path[(i + 1)..] : path;
    }

    private static string TransferRoute(TransferRequest req) =>
        req.Race && req.MeshSites.Count > 1
            ? "mesh"
            : req.FromSite + " > " + req.ToSite;

    private static List<string> ParseFeatures(string raw)
    {
        var features = new List<string>();
        foreach (var rawLine in raw.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("Features") || line.StartsWith("End")) continue;
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length == 0) continue;
            features.Add(fields[0].ToUpperInvariant());
        }
        return features;
    }

    private static List<string> CompleteMarkersFor(Site site)
    {
        var markers = (site.CompleteMarkers ?? WeaveFxp.Engine.Models.Site.DefaultCompleteMarkers())
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return markers.Count == 0 ? WeaveFxp.Engine.Models.Site.DefaultCompleteMarkers() : markers;
    }

    private static bool CompletionMarkerMatches(string name, string marker)
    {
        name = (name ?? "").Trim();
        marker = (marker ?? "").Trim();
        if (name.Length == 0 || marker.Length == 0) return false;

        if (marker.Contains('*') || marker.Contains('?'))
        {
            var wildcard = "^" + Regex.Escape(marker)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, wildcard, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        if (name.Equals(marker, StringComparison.OrdinalIgnoreCase)) return true;
        var token = $@"(^|[^A-Za-z0-9]){Regex.Escape(marker)}([^A-Za-z0-9]|$)";
        return Regex.IsMatch(name, token, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool SkiplistMatches(string path, string name, IEnumerable<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            var p = (pattern ?? "").Trim();
            if (p.Length == 0) continue;
            if (SkipPatternMatches(name, p) || SkipPatternMatches(path, p))
                return true;
        }
        return false;
    }

    private static bool SkipPatternMatches(string value, string pattern)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return false;
        if (pattern.Contains('*') || pattern.Contains('?'))
        {
            var wildcard = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            return Regex.IsMatch(value, wildcard, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return value.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> MergePatternLists(params IEnumerable<string>?[] lists)
    {
        return lists
            .Where(x => x is not null)
            .SelectMany(x => x!)
            .Select(x => (x ?? "").Trim())
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<Site> ApplySiteOrder(List<Site> sites, IEnumerable<string>? order)
    {
        var ranks = MergePatternLists(order)
            .Select((name, index) => (name, index))
            .ToDictionary(x => x.name, x => x.index, StringComparer.OrdinalIgnoreCase);

        return sites
            .OrderBy(s => ranks.TryGetValue(s.Name, out var rank) ? rank : int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsIgnorableDirectoryMiss(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("550", StringComparison.OrdinalIgnoreCase) &&
            (message.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("failed", StringComparison.OrdinalIgnoreCase));
    }
}
