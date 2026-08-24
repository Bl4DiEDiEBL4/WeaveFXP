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
public sealed class WeaveEngine
{
    private const int MaxLogEntries = 3000;

    private readonly JsonStore _store;
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
        // them, prune only after long inactivity. This is what lets the first STOR of
        // an announce fire in milliseconds instead of after a fresh TCP+TLS+login.
        _poolSweepTimer = new System.Threading.Timer(_ => { _ = SweepPoolsAsync(); }, null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private readonly System.Threading.Timer? _poolSweepTimer;

    private async Task SweepPoolsAsync()
    {
        List<SitePool> pools;
        lock (_poolLock) pools = _pools.Values.ToList();
        foreach (var pool in pools)
        {
            try { await pool.SweepAsync(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(45)).ConfigureAwait(false); }
            catch { /* best-effort keepalive */ }
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
                ?? "1.0.1";
            var metadata = version.IndexOf('+');
            return metadata < 0 ? version : version[..metadata];
        }
    }

    private void NotifyChanged() => Changed?.Invoke();

    // Coalesced UI notification. Protocol chatter can fire hundreds of times a second;
    // re-rendering the whole browser on each one is what makes the UI crawl. At most
    // ~8 notifications/sec, with a trailing one so the last state always lands.
    private long _lastNotifyTicks;
    private int _notifyPending;
    private void NotifyChangedThrottled()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _lastNotifyTicks);
        if (now - last >= TimeSpan.TicksPerMillisecond * 125)
        {
            Interlocked.Exchange(ref _lastNotifyTicks, now);
            NotifyChanged();
            return;
        }
        if (Interlocked.CompareExchange(ref _notifyPending, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(125).ConfigureAwait(false);
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
        public DateTime LastNotify = DateTime.MinValue;
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

        // Throttle UI refreshes to ~4/sec.
        if ((now - win.LastNotify).TotalMilliseconds >= 250)
        {
            win.LastNotify = now;
            NotifyChanged();
        }
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
        if (settings.CreatedAt == default) settings.CreatedAt = current.CreatedAt;
        var saved = _store.UpdateSettings(settings);
        Log("system", "settings", "info", "settings saved");
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

    public bool RemoveJob(string id)
    {
        var job = _store.Job(id);
        if (job is null) return false;
        if (!job.Terminal)
            CancelJobInternal(id, "Removed from queue");
        return _store.DeleteJob(id);
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
            j.FilesDone = 0; j.FilesTotal = 0; j.CurrentFile = "";
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
            ArmJobWatchdog(id, run);
            _ = Task.Run(() => RunDownloadJobAsync(id, req, run));
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
            ArmJobWatchdog(id, run);
            _ = Task.Run(() => RunUploadJobAsync(id, req, run));
            return true;
        }

        ArmJobWatchdog(id, run);
        _ = Task.Run(() => RunTransferJobAsync(id, existing.Request, run.Token, run));
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

        foreach (var entry in entries)
        {
            var lower = entry.Name.ToLowerInvariant();
            foreach (var marker in markers)
            {
                if (CompletionMarkerMatches(entry.Name, marker))
                {
                    check.Markers.Add(entry.Name);
                    check.State = ReleaseState.Complete;
                    break;
                }
            }
            if (lower.EndsWith(".sfv") && entry.Type != "dir")
            {
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
                foreach (var file in Sfv.Parse(raw))
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
            check.Description = "completion marker visible";
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
        ArmJobWatchdog(job.Id, run);
        _ = Task.Run(() => RunTransferJobAsync(job.Id, req, run.Token, run));
        return job;
    }

    public SpreadResult StartSpread(SpreadRequest req)
    {
        var result = CreateSpread(req);
        _ = Task.Run(() => RunSpreadAsync(result.Jobs, result.MaxParallel));
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
        Log("transfer", req.FromSite + " > " + req.ToSite, "info",
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

            if (names.Count <= 2)
            {
                var src = sites.First(s => s.Name.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase));
                var dst = sites.First(s => !s.Name.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase));
                if (!TransferAllowed(src, dst))
                    throw new IOException($"race route {src.Name} -> {dst.Name} is blocked");
                jobs.Add(CreateTransferJob(new TransferRequest
                {
                    BatchId = batchId,
                    FromSite = src.Name,
                    ToSite = dst.Name,
                    SourcePath = req.SourcePath,
                    DestPath = req.DestPath,
                    Race = true,
                    DryRun = req.DryRun,
                    ViaApi = req.ViaApi,
                    Label = label0,
                }));
            }
            else
            {
                jobs.Add(CreateTransferJob(new TransferRequest
                {
                    BatchId = batchId,
                    FromSite = req.FromSite,
                    ToSite = "mesh",
                    SourcePath = req.SourcePath,
                    DestPath = req.DestPath,
                    MeshSites = names,
                    Race = true,
                    DryRun = req.DryRun,
                    ViaApi = req.ViaApi,
                    Label = label0,
                }));
            }
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
                    SourcePath = req.SourcePath,
                    DestPath = req.DestPath,
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
                if (req.MeshSites.Count > 1)
                {
                    var meshComplete = await RunMeshRaceLoopAsync(id, req, ct).ConfigureAwait(false);
                    FinishJob(id, meshComplete ? null : new IOException("mesh race stopped idle before completion"), run);
                    return meshComplete;
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
        public HashSet<string> MadeDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SemaphoreSlim DirSem { get; } = new(1, 1);
    }

    private readonly struct MeshPick
    {
        public MeshPick(MeshSiteCtx src, MeshSiteCtx dst, RaceFile file)
        { Src = src; Dst = dst; File = file; }
        public MeshSiteCtx Src { get; }
        public MeshSiteCtx Dst { get; }
        public RaceFile File { get; }
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

        string PathOn(string site) => site.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) ? req.SourcePath : req.DestPath;

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
                var slots = Math.Max(c.Site.DownloadSlots, c.Site.UploadSlots);
                if (slots <= 1) slots = 3;
                _ = c.Pool.WarmUpAsync(Math.Min(c.Pool.Max, Math.Max(2, slots + 2)), ct)
                    .ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }

            var started = DateTime.UtcNow;
            var sync = new object();
            var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var attempts = new Dictionary<string, Attempt>(StringComparer.OrdinalIgnoreCase);
            var attemptsLock = new object();
            var knownSizes = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            using var stopWorkers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var workSignal = new SemaphoreSlim(0);
            var raceDone = 0;
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
                if (site.Confirmed.Contains(rel)) return true; // transferred here / proven by X-DUPE
                if (!site.Files.TryGetValue(rel, out var f)) return false;
                return f.Size > 0 && (sourceSize <= 0 || f.Size >= sourceSize);
            }

            bool RouteAllowed(MeshSiteCtx src, MeshSiteCtx dst)
            {
                if (src.Name.Equals(dst.Name, StringComparison.OrdinalIgnoreCase)) return false;
                if (!src.Site.AllowDownload || !dst.Site.AllowUpload) return false;
                return TransferAllowed(src.Site, dst.Site);
            }

            MeshPick? TakeBest()
            {
                lock (sync)
                {
                    var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                    MeshPick? best = null;
                    var bestScore = int.MinValue;
                    foreach (var src in contexts)
                    {
                        foreach (var f in src.Files.Values)
                        {
                            if (IsUnreadableSfv(f)) continue;
                            var sourceSize = knownSizes.TryGetValue(f.Rel, out var sz) ? sz : f.Size;
                            if (!HasComplete(src, f.Rel, sourceSize)) continue;
                            foreach (var dst in contexts)
                            {
                                if (!RouteAllowed(src, dst)) continue;
                                if (HasComplete(dst, f.Rel, sourceSize)) continue;
                                var key = f.Rel + "|" + dst.Name;
                                if (inFlight.Contains(key)) continue;
                                lock (attemptsLock) { if (InBackoff(attempts, key, nowMs) || AttemptsExceeded(attempts, key)) continue; }
                                var score = RaceScore(f.Name) * 1000 + (int)Math.Min(999, Math.Max(0, f.Size / 1024 / 1024));
                                if (score <= bestScore) continue;
                                bestScore = score;
                                best = new MeshPick(src, dst, f);
                            }
                        }
                    }
                    if (best is not null)
                        inFlight.Add(best.Value.File.Rel + "|" + best.Value.Dst.Name);
                    return best;
                }
            }

            void FinishPick(MeshPick pick, bool requeue)
            {
                lock (sync) inFlight.Remove(pick.File.Rel + "|" + pick.Dst.Name);
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
                }
                Interlocked.Add(ref cumulative, Math.Max(0, pick.File.Size));
                var cum = Interlocked.Read(ref cumulative);
                var now = DateTime.UtcNow;
                lock (speedLock) recentTransfers.Add((startedAt, now, Math.Max(0, pick.File.Size)));
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
                        var files = await ListSourceFilesAsync(conn, ctx.Path, ctx.Skiplist, ct).ConfigureAwait(false);
                        ctx.Pool.Return(conn);
                        conn = null;
                        listFails = 0;
                        lock (sync)
                        {
                            foreach (var f in files)
                            {
                                knownSizes.AddOrUpdate(f.Rel, Math.Max(0, f.Size), (_, old) => Math.Max(old, Math.Max(0, f.Size)));
                                if (!ctx.Files.TryGetValue(f.Rel, out var old) || old.Size != f.Size)
                                {
                                    ctx.Files[f.Rel] = f;
                                    added++;
                                }
                            }
                        }
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
                while (!ct.IsCancellationRequested && Volatile.Read(ref raceDone) == 0)
                {
                    poll++;
                    int knownFiles, inFlightCount;
                    lock (sync)
                    {
                        knownFiles = contexts.SelectMany(c => c.Files.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                        inFlightCount = inFlight.Count;
                    }
                    _store.UpdateJobTransient(id, j =>
                    {
                        j.FilesTotal = Math.Max(j.FilesTotal, knownFiles);
                        j.SpeedBps = CurrentSpeed(DateTime.UtcNow);
                    });
                    if (poll % 20 == 1)
                        LogJobLive(id, "info", $"mesh poll #{poll}: {knownFiles} file(s), {inFlightCount} in flight, {sentCount} raced");
                    if (inFlightCount == 0)
                    {
                        idleCycles++;
                        idleSince ??= DateTime.UtcNow;
                        if (DateTime.UtcNow - idleSince.Value >= idleTimeout)
                        {
                            LogJob(id, "info", $"mesh race stopped after {(DateTime.UtcNow - idleSince.Value).TotalSeconds:0.0}s idle with no work");
                            Volatile.Write(ref raceDone, 1);
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
                    var pick = TakeBest();
                    if (pick is null)
                    {
                        if (Volatile.Read(ref raceDone) != 0) return;
                        try { await workSignal.WaitAsync(wakeMs, wct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        continue;
                    }

                    FtpClient? s = null, d = null;
                    var srcOk = true; var dstOk = true; var requeue = false; var cancelled = false;
                    var slowSkipped = false;
                    var xferStart = DateTime.UtcNow;
                    try
                    {
                        s = await pick.Value.Src.Pool.TryBorrowTransferAsync(wct).ConfigureAwait(false);
                        if (s is null)
                        {
                            FinishPick(pick.Value, requeue: true);
                            await pick.Value.Src.Pool.WaitForTransferAvailabilityAsync(TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }
                        d = await pick.Value.Dst.Pool.TryBorrowTransferAsync(wct).ConfigureAwait(false);
                        if (d is null)
                        {
                            pick.Value.Src.Pool.ReturnTransfer(s);
                            s = null;
                            FinishPick(pick.Value, requeue: true);
                            await pick.Value.Dst.Pool.WaitForTransferAvailabilityAsync(TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }

                        await pick.Value.Dst.DirSem.WaitAsync(ct).ConfigureAwait(false);
                        try { await EnsureDestDirAsync(d, pick.Value.Dst.Path, pick.Value.File.ParentRel, pick.Value.Dst.MadeDirs, id).ConfigureAwait(false); }
                        finally { pick.Value.Dst.DirSem.Release(); }

                        var absDst = FtpClient.JoinRemote(pick.Value.Dst.Path, pick.Value.File.Rel);
                        LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: sending {pick.Value.File.Rel} ({HumanBytes(pick.Value.File.Size)})");
                        _store.UpdateJobTransient(id, j =>
                        {
                            j.CurrentFile = pick.Value.File.Name;
                            var row = new FileTransfer { Name = pick.Value.File.Rel, Size = Math.Max(0, pick.Value.File.Size), StartedAt = xferStart, Status = "active" };
                            j.Files.Add(row);
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
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel && x.Status == "active");
                            if (row is not null)
                            {
                                row.Status = "done";
                                row.Seconds = dur;
                                row.Bps = row.Size / dur;
                            }
                        });
                        RecordSuccess(pick.Value, xferStart);
                        LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: raced {pick.Value.File.Rel} in {dur:0.00}s");
                        _store.AddSiteTraffic(pick.Value.Src.Name, pick.Value.File.Size, 0, dur);
                        _store.AddSiteTraffic(pick.Value.Dst.Name, 0, pick.Value.File.Size, dur);
                    }
                    catch (Exception ex) when (slowSkipped)
                    {
                        // We aborted it for stalling. Connections carry unread ABOR replies —
                        // drop them. Retry later; counts toward the give-up cap.
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var key = pick.Value.File.Rel + "|" + pick.Value.Dst.Name;
                        var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) RecordFail(attempts, key, nowMs);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel && x.Status == "active");
                            if (row is not null) { row.Status = "slow"; row.Error = FirstLineOf(ex.Message); row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds); }
                        });
                        LogJobLive(id, "warn", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: aborted {pick.Value.File.Name}: stalled");
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        cancelled = true; srcOk = false; dstOk = false;
                    }
                    catch (Exception ex) when (FxpTransfer.IsBeingUploaded(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        requeue = true;
                        _ = ex;
                    }
                    catch (Exception ex) when (FxpTransfer.IsSkippableTransferError(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
                        // Mark this file present on dst, AND learn the whole dupe batch from
                        // the one X-DUPE refusal (other files already on dst in this dir) so
                        // we don't pay a failed STOR round trip for each of them.
                        var learned = 0;
                        lock (sync)
                        {
                            pick.Value.Dst.Confirmed.Add(pick.Value.File.Rel);
                            foreach (var dupeName in FxpTransfer.ParseXdupeNames(ex))
                            {
                                var rel = string.IsNullOrEmpty(pick.Value.File.ParentRel) ? dupeName : pick.Value.File.ParentRel + "/" + dupeName;
                                if (SkiplistMatches(rel, dupeName, pick.Value.Dst.Skiplist)) continue;
                                if (pick.Value.Dst.Confirmed.Add(rel)) learned++;
                            }
                        }
                        LogJobLive(id, "info", $"{pick.Value.Src.Name} > {pick.Value.Dst.Name}: skipped {pick.Value.File.Name}: dupe{(learned > 0 ? $" (+{learned} more via X-DUPE)" : "")}");
                    }
                    catch (Exception ex)
                    {
                        srcOk = false; dstOk = false;
                        requeue = true;
                        var key = pick.Value.File.Rel + "|" + pick.Value.Dst.Name;
                        var nowMs = (DateTime.UtcNow - started).TotalMilliseconds;
                        lock (attemptsLock) RecordFail(attempts, key, nowMs);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = j.Files.LastOrDefault(x => x.Name == pick.Value.File.Rel && x.Status == "active");
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
                        if (s is not null) { if (srcOk) pick.Value.Src.Pool.ReturnTransfer(s); else pick.Value.Src.Pool.DropTransfer(s); }
                        if (d is not null) { if (dstOk) pick.Value.Dst.Pool.ReturnTransfer(d); else pick.Value.Dst.Pool.DropTransfer(d); }
                        FinishPick(pick.Value, requeue && !cancelled);
                    }
                }
            }

            LogJob(id, "info", $"mesh race started with {contexts.Count} site(s), poll every {pollMs}ms, stop after ~{FormatDuration(idleTimeout)} idle");
            var listers = contexts.Select(ListerAsync).ToList();
            var workerCount = Math.Clamp(contexts.Sum(c => Math.Max(1, Math.Min(ResolveRaceSlots(c.Site, c.Site), c.Pool.Max - 1))), 1, 64);
            var workers = Enumerable.Range(1, workerCount).Select(WorkerAsync).ToList();
            var coordinator = CoordinatorAsync();
            await Task.WhenAll(listers.Concat(workers).Append(coordinator)).ConfigureAwait(false);
            return sentCount > 0;
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

            // Ensure the destination release root exists — CONCURRENTLY with the source
            // lister, so the borrow+MKD round trips never sit on the announce-critical
            // path. Workers await this before their first transfer. Retried: a transient
            // dial failure at t=0 must not kill an announce race.
            var destSetup = Task.Run(async () =>
            {
                try
                {
                    for (var attempt = 1; ; attempt++)
                    {
                        FtpClient? rootConn = null;
                        try
                        {
                            rootConn = await dstPool.BorrowAsync(ct).ConfigureAwait(false);
                            await EnsureDestDirAsync(rootConn, req.DestPath, "", madeDirs, id).ConfigureAwait(false);
                            dstPool.Return(rootConn);
                            return;
                        }
                        catch (OperationCanceledException) { if (rootConn is not null) dstPool.Drop(rootConn); throw; }
                        catch (Exception ex)
                        {
                            if (rootConn is not null) dstPool.Drop(rootConn);
                            if (attempt >= 3) throw;
                            LogJobLive(id, "warn", $"dest setup failed (attempt {attempt}): {FirstLineOf(ex.Message)} — retrying");
                            await Task.Delay(1000, ct).ConfigureAwait(false);
                        }
                    }
                }
                catch
                {
                    Volatile.Write(ref raceDone, 2); // stop the lister; the WhenAll below rethrows
                    throw;
                }
            }, ct);

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

                    var expectedRows = new List<(string Rel, long Size, string Status, string Error)>();
                    foreach (var file in Sfv.Parse(raw))
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
                    foreach (var row in expectedRows)
                        LogJobLive(id, "info", $"expected {row.Rel} from {sfvFile.Rel}");
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
                        // A failed BORROW (dial refused, "530 too many connections", …)
                        // must never crash the lister — that used to fail the whole race.
                        var files = new List<RaceFile>();
                        FtpClient? lister = null;
                        try
                        {
                            lister = await srcPool.BorrowAsync(ct).ConfigureAwait(false);
                            var listSw = System.Diagnostics.Stopwatch.StartNew();
                            files = await ListSourceFilesAsync(lister, req.SourcePath, skiplist, ct).ConfigureAwait(false);
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
                                    // seen (source mid-upload) — refresh its size.
                                    var idx = pending.FindIndex(p => p.Rel.Equals(f.Rel, StringComparison.OrdinalIgnoreCase));
                                    if (idx >= 0 && pending[idx].Size != f.Size) pending[idx] = f;
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

                        if (files.Count != lastFound || poll % 30 == 1)
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
                            else
                            {
                                int expectedCount;
                                lock (sync) expectedCount = expectedFromSfv.Count;
                                if (expectedCount == 0 && DateTime.UtcNow >= nextCompletionProbe)
                                {
                                    nextCompletionProbe = DateTime.UtcNow.AddSeconds(2);
                                    complete = await TryCompletionProbeAsync().ConfigureAwait(false);
                                }
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

                        // Hot (files moving or just appeared): hammer the source like
                        // New pieces can land every few hundred ms.
                        // Quiet: back off gradually so we don't pound an idle dir.
                        var delay = added > 0 ? pollMs
                                  : inFlightCount > 0 || pendingCount > 0 ? Math.Min(pollMs, 250)
                                  : idleCycles <= 5 ? pollMs
                                  : idleCycles <= 15 ? pollMs * 2
                                  : pollMs * 4;
                        try { await Task.Delay(Math.Min(delay, 30000), ct).ConfigureAwait(false); }
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
                // Nothing can land before the release root exists on dest; its setup runs
                // in parallel with the lister. A setup failure is reported by destSetup.
                try { await destSetup.ConfigureAwait(false); } catch { return; }
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
                        s = await srcPool.TryBorrowTransferAsync(wct).ConfigureAwait(false);
                        if (s is null)
                        {
                            Interlocked.Increment(ref noSrcSlot);
                            FinishFile(f, requeue: true);
                            await srcPool.WaitForTransferAvailabilityAsync(TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }
                        d = await dstPool.TryBorrowTransferAsync(wct).ConfigureAwait(false);
                        if (d is null)
                        {
                            Interlocked.Increment(ref noDstSlot);
                            srcPool.ReturnTransfer(s);
                            s = null;
                            FinishFile(f, requeue: true);
                            await dstPool.WaitForTransferAvailabilityAsync(TimeSpan.FromMilliseconds(pollMs), wct).ConfigureAwait(false);
                            continue;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (d is not null) dstPool.ReturnTransfer(d);
                        if (s is not null) srcPool.ReturnTransfer(s);
                        FinishFile(f, requeue: false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (d is not null) dstPool.ReturnTransfer(d);
                        if (s is not null) srcPool.ReturnTransfer(s);
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
                                row = new FileTransfer { Name = f.Rel, Size = Math.Max(0, f.Size), StartedAt = xferStart };
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
                        // Retry hot: 350ms routinely handed the completed piece to another
                        // racer even though our listing had discovered it first.
                        notBefore[f.Rel] = DateTime.UtcNow.AddMilliseconds(Math.Clamp(pollMs * 2, 50, 150));
                        FileRow("wait", "still uploading on source");
                        _ = ex;
                    }
                    catch (Exception ex) when (FxpTransfer.IsSkippableTransferError(ex))
                    {
                        if (FxpTransfer.RequiresConnectionDrop(ex)) { srcOk = false; dstOk = false; }
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
                        if (srcOk) srcPool.ReturnTransfer(s); else srcPool.DropTransfer(s);
                        if (dstOk) dstPool.ReturnTransfer(d); else dstPool.DropTransfer(d);
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
                try { await destSetup.ConfigureAwait(false); } catch { return; } // needs the final dest root
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
            workerTasks.Add(destSetup);
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
            var max = site.LoginSlots > 1
                ? site.LoginSlots
                : Math.Max(3, Math.Max(site.DownloadSlots, site.UploadSlots)) + 1;
            max = Math.Clamp(max, 2, 40);
            var fp = PoolFingerprint(cfg, max);
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
                pool = new SitePool(cfg, max, fp);
                _pools[name] = pool;
            }
            _poolRefs[name] = (_poolRefs.TryGetValue(name, out var n) ? n : 0) + 1;
            return pool;
        }
    }

    private static string PoolFingerprint(FtpClient.Config c, int max) =>
        string.Join('|', c.Host, c.Port, c.Username, c.Password, c.TlsMode, c.UseEpsv, c.UsePret, c.UseSscn,
            c.FxpMode, c.PassiveHost, c.ListCommand, c.ForceBinary, c.BrokenPasv, c.UseXdupe, c.XdupeMode,
            c.TimeoutSeconds, c.CwdBeforeStatListing, max);

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
    private sealed class SitePool
    {
        public FtpClient.Config Cfg { get; }
        private readonly SemaphoreSlim _gate;
        // Unlike _gate (concurrent borrowers), this permit stays held for the full
        // lifetime of a physical FTP session. Without it overlapping warmups could
        // leave more logged-in idle clients than the site's configured login limit.
        private readonly SemaphoreSlim _openGate;
        private readonly SemaphoreSlim _transferGate;
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

        public SitePool(FtpClient.Config cfg, int max, string fingerprint = "")
        {
            Cfg = cfg;
            Max = Math.Max(1, max);
            _gate = new SemaphoreSlim(Max, Max);
            _openGate = new SemaphoreSlim(Max, Max);
            var transferMax = Math.Max(1, Max - Math.Min(ReservedControlSlots, Max - 1));
            _transferGate = new SemaphoreSlim(transferMax, transferMax);
            Fingerprint = fingerprint;
        }

        private void SignalAvailability() => _availability.Release();

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
        public async Task<FtpClient?> TryBorrowAsync(CancellationToken ct)
        {
            if (!_gate.Wait(0)) return null;
            return await TakeOrOpenAsync(ct).ConfigureAwait(false);
        }

        public async Task<FtpClient?> TryBorrowTransferAsync(CancellationToken ct)
        {
            if (!_transferGate.Wait(0)) return null;
            if (!_gate.Wait(0))
            {
                _transferGate.Release();
                return null;
            }
            try
            {
                return await TakeOrOpenAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // TakeOrOpenAsync already returns the login permit on failure.
                _transferGate.Release();
                SignalAvailability();
                throw;
            }
        }

        public async Task WaitForTransferAvailabilityAsync(TimeSpan timeout, CancellationToken ct)
        {
            await _availability.WaitAsync(timeout, ct).ConfigureAwait(false);
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
                var dials = Enumerable.Range(0, need).Select(async _ =>
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
            finally { _warmupGate.Release(); }
        }

        public void Return(FtpClient c) { _idle.Add((c, DateTime.UtcNow)); _gate.Release(); SignalAvailability(); }
        public void Drop(FtpClient c) { try { c.Dispose(); } catch { } _openGate.Release(); _gate.Release(); SignalAvailability(); }
        public void ReturnTransfer(FtpClient c) { _idle.Add((c, DateTime.UtcNow)); _gate.Release(); _transferGate.Release(); SignalAvailability(); }
        public void DropTransfer(FtpClient c) { try { c.Dispose(); } catch { } _openGate.Release(); _gate.Release(); _transferGate.Release(); SignalAvailability(); }
        public void DisposeAll()
        {
            while (_idle.TryTake(out var e))
            {
                try { e.Client.Dispose(); } catch { }
                _openGate.Release();
            }
        }
    }

    // Slots per race = min(source download slots, dest upload slots)
    // the site connection pools. Defaults to 3 when a site leaves the field at 0/1,
    // clamped to a sane ceiling so we never hammer a box.
    private static int ResolveRaceSlots(Site srcSite, Site dstSite)
    {
        // Treat the default 0/1 as "unset" and race 3-wide; honor explicit >1 limits.
        // The site's configured slots are the real cap; the only hard
        // ceiling here is a sanity guard.
        var srcSlots = srcSite.DownloadSlots > 1 ? srcSite.DownloadSlots : 3;
        var dstSlots = dstSite.UploadSlots > 1 ? dstSite.UploadSlots : 3;
        if (srcSite.LoginSlots > 1) srcSlots = Math.Min(srcSlots, srcSite.LoginSlots);
        if (dstSite.LoginSlots > 1) dstSlots = Math.Min(dstSlots, dstSite.LoginSlots);
        return Math.Clamp(Math.Min(srcSlots, dstSlots), 1, 30);
    }

    // Recursively list files under root over an open source connection, skipping
    // directories/links traversal control, skiplist matches and -missing markers.
    private async Task<List<RaceFile>> ListSourceFilesAsync(FtpClient src, string root, List<string> skiplist, CancellationToken ct)
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
            catch
            {
                // A subdir we can't enter (glftpd tag/status dir, race-condition removal,
                // permission) shouldn't abort the whole walk — just skip it silently.
                return;
            }
            foreach (var e in entries)
            {
                if (e.Name is "." or "..") continue;
                var childAbs = FtpClient.JoinRemote(absDir, e.Name);
                var childRel = relDir.Length == 0 ? e.Name : relDir + "/" + e.Name;
                if (e.Type is "dir" or "link")
                {
                    if (IsVirtualDir(e.Name)) continue;                 // glftpd status/tag "dirs"
                    if (SkiplistMatches(childAbs, e.Name, skiplist)) continue;
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
        ArmJobWatchdog(saved.Id, run);
        _ = Task.Run(() => RunDownloadJobAsync(saved.Id, req, run));
        return saved;
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

    private int ResolveDownloadSlots(Site site)
    {
        var settings = _store.Settings();
        var slots = site.DownloadSlots > 1 ? site.DownloadSlots : 3;
        slots = Math.Min(slots, settings.LocalDownloadSlots);
        if (site.LoginSlots > 1) slots = Math.Min(slots, site.LoginSlots);
        return Math.Clamp(slots, 1, Math.Max(1, settings.LocalDownloadSlots));
    }

    private async Task RunDownloadJobAsync(string id, DownloadRequest req, JobRunControl run)
    {
        LogJob(id, "info", "download started");
        _store.UpdateJob(id, j => { j.State = JobState.Running; j.StartedAt = DateTime.UtcNow; });
        NotifyChanged();
        var ct = run.Token;
        try
        {
            var site = _store.Site(req.Site) ?? throw new IOException($"site \"{req.Site}\": not found");
            var cfg = FtpConfig(site, "", !req.ViaApi);
            var job = _store.Job(id) ?? throw new IOException("job vanished");
            var dest = job.Request.DestPath;
            var settings = _store.Settings();
            var skiplist = MergePatternLists(settings.GlobalSkiplist, site.Skiplist);
            var skipEmptyFolders = settings.SkipEmptyFolders;

            if (SkiplistMatches(req.SourcePath, RemoteBase(req.SourcePath), skiplist))
            {
                LogJob(id, "info", $"skiplist skipped {req.SourcePath}");
                FinishJob(id, null, run);
                return;
            }

            // Phase 1: collect the full file list over one connection.
            var files = new List<DlFile>();
            using (var lister = await FtpClient.DialAndLoginAsync(cfg, ct).ConfigureAwait(false))
            {
                var (code, _) = await lister.CommandAsync("CWD " + req.SourcePath).ConfigureAwait(false);
                if (code / 100 == 2)
                    await CollectDownloadFilesAsync(lister, id, req.SourcePath, dest, 16, skiplist, skipEmptyFolders, files, ct).ConfigureAwait(false);
                else
                    files.Add(new DlFile(req.SourcePath, dest, -1));
            }

            var knownBytes = files.Where(f => f.Size > 0).Sum(f => f.Size);
            _store.UpdateJobTransient(id, j => { j.FilesTotal = files.Count; j.BytesTotal = knownBytes; });

            // Phase 2: drain the list across N parallel connections ("threads"),
            // count from the site's Download slots setting.
            var slotCount = Math.Min(ResolveDownloadSlots(site), Math.Max(1, files.Count));
            LogJob(id, "info", $"downloading {files.Count} file(s) with {slotCount} thread(s)");

            var queue = new ConcurrentQueue<DlFile>(files);
            var slotStates = new SlotProgress[slotCount];
            for (var i = 0; i < slotCount; i++) slotStates[i] = new SlotProgress { Slot = i + 1 };
            long doneBytes = 0;
            var filesDone = 0;
            Exception? firstErr = null;
            var stateLock = new object();
            var lastPush = DateTime.MinValue;

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
                try
                {
                    conn = await FtpClient.DialAndLoginAsync(cfg, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested && queue.TryDequeue(out var f))
                    {
                        await WaitWhilePausedAsync(id, ct).ConfigureAwait(false);
                        var name = RemoteBase(f.Remote);
                        var size = f.Size;
                        if (size <= 0) size = await conn.SizeAsync(f.Remote).ConfigureAwait(false);
                        lock (stateLock) { slot.File = name; slot.Done = 0; slot.Total = Math.Max(0, size); slot.Bps = 0; }
                        LogJob(id, "info", $"[T{idx + 1}] downloading {f.Remote}");

                        var dir = Path.GetDirectoryName(f.Local);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

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
                            var dlStart = DateTime.UtcNow;
                            await using var fileStream = File.Create(f.Local);
                            var written = await conn.RetrieveToAsync(f.Remote, fileStream, ct, progress).ConfigureAwait(false);
                            lock (stateLock)
                            {
                                doneBytes += written;
                                filesDone++;
                                slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0;
                            }
                            _store.AddSiteTraffic(req.Site, written, 0, (DateTime.UtcNow - dlStart).TotalSeconds);
                            LogJob(id, "info", $"[T{idx + 1}] downloaded {name} ({written} bytes)");
                            Push(true);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; firstErr ??= ex; }
                            LogJob(id, "error", $"[T{idx + 1}] {name}: {FirstLineOf(ex.Message)}");
                            // The connection may be broken — replace it and keep going.
                            try { conn.Dispose(); } catch { }
                            conn = await FtpClient.DialAndLoginAsync(cfg, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    conn?.Dispose();
                    lock (stateLock) { slot.File = ""; slot.Done = 0; slot.Total = 0; slot.Bps = 0; }
                }
            }

            await Task.WhenAll(Enumerable.Range(0, slotCount).Select(WorkerAsync)).ConfigureAwait(false);
            Push(true);
            _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
            if (firstErr is not null)
                throw new IOException($"download finished with errors: {firstErr.Message}", firstErr);
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
        ArmJobWatchdog(saved.Id, run);
        _ = Task.Run(() => RunUploadJobAsync(saved.Id, req, run));
        return saved;
    }

    private sealed record UlFile(string Local, string Remote, long Size);

    private int ResolveUploadSlots(Site site)
    {
        var settings = _store.Settings();
        var slots = site.UploadSlots > 1 ? site.UploadSlots : 3;
        slots = Math.Min(slots, settings.LocalUploadSlots);
        if (site.LoginSlots > 1) slots = Math.Min(slots, site.LoginSlots);
        return Math.Clamp(slots, 1, Math.Max(1, settings.LocalUploadSlots));
    }

    private async Task RunUploadJobAsync(string id, UploadRequest req, JobRunControl run)
    {
        LogJob(id, "info", "upload started");
        _store.UpdateJob(id, j => { j.State = JobState.Running; j.StartedAt = DateTime.UtcNow; });
        NotifyChanged();
        var ct = run.Token;
        try
        {
            var site = _store.Site(req.Site) ?? throw new IOException($"site \"{req.Site}\": not found");
            var cfg = FtpConfig(site, "", !req.ViaApi);
            var job = _store.Job(id) ?? throw new IOException("job vanished");
            var files = CollectUploadFiles(req.SourcePath, job.Request.DestPath);
            var knownBytes = files.Where(f => f.Size > 0).Sum(f => f.Size);
            _store.UpdateJobTransient(id, j => { j.FilesTotal = files.Count; j.BytesTotal = knownBytes; });

            var slotCount = Math.Min(ResolveUploadSlots(site), Math.Max(1, files.Count));
            LogJob(id, "info", $"uploading {files.Count} file(s) with {slotCount} thread(s)");

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
                try
                {
                    conn = await FtpClient.DialAndLoginAsync(cfg, ct).ConfigureAwait(false);
                    while (!ct.IsCancellationRequested && queue.TryDequeue(out var f))
                    {
                        await WaitWhilePausedAsync(id, ct).ConfigureAwait(false);
                        var name = Path.GetFileName(f.Local);
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
                            try { conn.Dispose(); } catch { }
                            conn = await FtpClient.DialAndLoginAsync(cfg, ct).ConfigureAwait(false);
                        }
                    }
                }
                finally
                {
                    conn?.Dispose();
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
                        Log("transfer", failed.Request.FromSite + " > " + failed.Request.ToSite, "error", reason);
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
        _store.UpdateJob(id, j => j.Paused = true);
        LogJob(id, "info", "job paused (finishes the file in flight, then waits)");
        return true;
    }

    public bool ResumeJob(string id)
    {
        _jobPaused.TryRemove(id, out _);
        var job = _store.UpdateJob(id, j => j.Paused = false);
        if (job is null) return false;
        LogJob(id, "info", "job resumed");
        return true;
    }

    private bool IsJobPaused(string id) => _jobPaused.ContainsKey(id);

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
        var job = _store.UpdateJob(id, j => j.Events.Add(new JobEvent
        {
            Time = DateTime.UtcNow,
            Level = level,
            Message = message,
        }));
        var route = job is null ? id : job.Request.FromSite + " > " + job.Request.ToSite;
        Log("transfer", route, level, message);
        NotifyChanged();
    }

    // Hot-path job log: appends the event in memory only — no state.json disk write
    // per line (LogJob persists on every call, which throttles a busy race). The next
    // persisting UpdateJob (e.g. FinishJob) flushes everything accumulated.
    private void LogJobLive(string id, string level, string message)
    {
        var job = _store.UpdateJobTransient(id, j =>
        {
            j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = level, Message = message });
        });
        var route = job is null ? id : job.Request.FromSite + " > " + job.Request.ToSite;
        Log("transfer", route, level, message); // Log() already throttles UI notify
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
            var route = job.Request.FromSite + " > " + job.Request.ToSite;
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
            var route = job.Request.FromSite + " > " + job.Request.ToSite;
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
