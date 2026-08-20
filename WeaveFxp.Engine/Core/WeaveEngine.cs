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

    // Raised whenever the log or a job changes, so the UI can refresh live.
    public event Action? Changed;

    public WeaveEngine(string? statePath = null)
    {
        _store = new JsonStore(string.IsNullOrWhiteSpace(statePath) ? DefaultStatePath() : statePath!);
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
    public string Version => Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "1.0.0";

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
            _logRing.Add(new LogEntry
            {
                Seq = _logSeq,
                Time = DateTime.UtcNow,
                Category = category,
                Site = site,
                Level = level,
                Message = message,
            });
            if (_logRing.Count > MaxLogEntries)
                _logRing.RemoveRange(0, _logRing.Count - MaxLogEntries);
        }
        NotifyChangedThrottled();
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
        if (existing.Type != JobType.Download && string.IsNullOrWhiteSpace(existing.Request.ToSite))
            return false;

        // Reuse the SAME job row: reset it in place and rerun, instead of spawning a
        // new line in the list. The old event log is kept, with a marker line.
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
            j.Files = new List<FileTransfer>();
            j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "info", Message = "— retry: job restarted —" });
        });
        if (reset is null) return false;
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
            ArmJobWatchdog(id);
            _ = Task.Run(() => RunDownloadJobAsync(id, req));
            return true;
        }

        var token = RegisterJobToken(id);
        ArmJobWatchdog(id);
        _ = Task.Run(() => RunTransferJobAsync(id, existing.Request, token));
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
        int count;
        lock (_logLock)
        {
            count = _logRing.Count;
            _logRing.Clear();
            _logSeq++;
        }
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
        lock (_logLock)
        {
            result.Logs = _logRing.Count;
            _logRing.Clear();
            _logSeq++;
        }
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
        var written = await client.RetrieveToAsync(path, ms, ct).ConfigureAwait(false);
        if (maxBytes > 0 && written > maxBytes)
            throw new IOException($"remote file {path} exceeds {maxBytes} bytes");
        return ms.ToArray();
    }

    public async Task DeleteRemotePathAsync(string siteName, string path, CancellationToken ct = default)
    {
        siteName = (siteName ?? "").Trim();
        path = (path ?? "").Trim();
        if (string.IsNullOrWhiteSpace(siteName)) throw new ArgumentException("site is required");
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required");
        var site = _store.Site(siteName) ?? throw new IOException($"site \"{siteName}\": not found");
        using var client = await FtpClient.DialAndLoginAsync(FtpConfig(site), ct).ConfigureAwait(false);

        var (deleteCode, deleteMsg) = await client.CommandAsync("DELE " + path).ConfigureAwait(false);
        if (deleteCode / 100 == 2)
        {
            Log("system", siteName, "warn", $"deleted remote file {path}");
            return;
        }

        var (removeCode, removeMsg) = await client.CommandAsync("RMD " + path).ConfigureAwait(false);
        if (removeCode / 100 == 2)
        {
            Log("system", siteName, "warn", $"deleted remote directory {path}");
            return;
        }

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
        var token = RegisterJobToken(job.Id);
        ArmJobWatchdog(job.Id);
        _ = Task.Run(() => RunTransferJobAsync(job.Id, req, token));
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
        var toSite = _store.Site(req.ToSite) ?? throw new IOException($"to_site \"{req.ToSite}\": not found");
        if (fromSite.BlockTransferFrom) throw new IOException($"site \"{req.FromSite}\" blocks transfers FROM it");
        if (toSite.BlockTransferTo) throw new IOException($"site \"{req.ToSite}\" blocks transfers TO it");
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
            // Race mesh: every site in the spread is both source AND destination
            // (1->2 and 2->1), so whoever has files feeds whoever doesn't. Per-site
            // "block transfer to/from" checkboxes filter directions out of the mesh.
            var names = new List<string> { req.FromSite };
            foreach (var raw in req.ToSites)
            {
                var t = raw.Trim();
                if (t.Length > 0 && !names.Any(n => n.Equals(t, StringComparison.OrdinalIgnoreCase)))
                    names.Add(t);
            }
            // The release path on each site: the announced SourcePath on the from-site,
            // the DestPath everywhere else.
            string PathOn(string site) => site.Equals(req.FromSite, StringComparison.OrdinalIgnoreCase) ? req.SourcePath : req.DestPath;

            foreach (var a in names)
            {
                var src = _store.Site(a) ?? throw new IOException($"site \"{a}\": not found");
                if (src.BlockTransferFrom) continue;
                foreach (var b in names)
                {
                    if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) continue;
                    var dst = _store.Site(b) ?? throw new IOException($"site \"{b}\": not found");
                    if (dst.BlockTransferTo) continue;
                    jobs.Add(CreateTransferJob(new TransferRequest
                    {
                        BatchId = batchId,
                        FromSite = a,
                        ToSite = b,
                        SourcePath = PathOn(a),
                        DestPath = PathOn(b),
                        Race = true,
                        DryRun = req.DryRun,
                        ViaApi = req.ViaApi,
                        Label = label0,
                    }));
                }
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
        return new SpreadResult { BatchId = batchId, MaxParallel = EffectiveSpreadParallel(req), Jobs = jobs };
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

                using var jobLinked = CancellationTokenSource.CreateLinkedTokenSource(raceStop.Token, RegisterJobToken(job.Id));
                ArmJobWatchdog(job.Id);
                var stopRace = await RunTransferJobAsync(job.Id, job.Request, jobLinked.Token).ConfigureAwait(false);
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

    private async Task<bool> RunTransferJobAsync(string id, TransferRequest req, CancellationToken ct = default)
    {
        LogJob(id, "info", "job started");
        _store.UpdateJob(id, j => { j.State = JobState.Running; j.StartedAt = DateTime.UtcNow; });
        NotifyChanged();

        if (req.DryRun)
        {
            LogJob(id, "info", "dry run completed without connecting to FTP sites");
            FinishJob(id, null);
            return false;
        }

        try
        {
            ct.ThrowIfCancellationRequested();
            // NOTE: no "is the destination already complete?" probe here. On an announce
            // race those round trips are pure delay at the most latency-critical moment
            // The race loop checks completion once
            // it goes idle instead, and dupes are skipped per file via X-DUPE anyway.

            var src = _store.Site(req.FromSite) ?? throw new IOException($"from_site \"{req.FromSite}\": not found");
            var dst = _store.Site(req.ToSite) ?? throw new IOException($"to_site \"{req.ToSite}\": not found");

            if (req.Race)
            {
                // Real racer: keep re-listing the source and moving new files as they land,
                // best-scored first, until the release is complete or the source goes idle.
                var raceComplete = await RunRaceLoopAsync(id, req, src, dst, ct).ConfigureAwait(false);
                FinishJob(id, null);
                return raceComplete;
            }

            await FxpTransfer.TransferAsync(FtpConfig(src, "", !req.ViaApi), FtpConfig(dst, "", !req.ViaApi), req,
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
            FinishJob(id, null);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            CancelJobInternal(id, "race batch stopped after completion marker");
            return false;
        }
        catch (Exception ex)
        {
            if (await ReleaseCompleteAfterTransferErrorAsync(id, req, ex).ConfigureAwait(false))
                return req.Race;

            FinishJob(id, ex);
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

    private sealed class Attempt { public int Count; public long LastFailMs; }

    private async Task<bool> RunRaceLoopAsync(string id, TransferRequest req, Site srcSite, Site dstSite, CancellationToken ct)
    {
        var settings = _store.Settings();
        var pollMs = settings.RacePollIntervalMs;
        var maxIdle = settings.RaceMaxIdleCycles;
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
            var wantSlots = Math.Max(1, Math.Min(ResolveRaceSlots(srcSite, dstSite), Math.Min(srcPool.Max - 1, dstPool.Max)));
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
            var poll = 0;
            var lastFound = -1;
            var listFails = 0;
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

            LogJob(id, "info", $"race started (up to {wantSlots} shared slot(s), poll every {pollMs}ms, stop after {maxIdle} idle cycles)");

            // Warm every connection we'll need CONCURRENTLY. Dialing + TLS + login is
            // ~1s per connection; doing that lazily and serially as workers start costs
            // seconds at exactly the moment a race is won or lost.
            var warm = Task.WhenAll(
                srcPool.WarmUpAsync(Math.Min(srcPool.Max, wantSlots + 4), ct),   // + lister & staging headroom
                dstPool.WarmUpAsync(Math.Min(dstPool.Max, wantSlots + 3), ct));

            // Ensure the destination release root exists up front, over a borrowed conn.
            // Retried: a transient dial failure at t=0 must not kill an announce race.
            for (var attempt = 1; ; attempt++)
            {
                FtpClient? rootConn = null;
                try
                {
                    rootConn = await dstPool.BorrowAsync(ct).ConfigureAwait(false);
                    await EnsureDestDirAsync(rootConn, req.DestPath, "", madeDirs, id).ConfigureAwait(false);
                    dstPool.Return(rootConn);
                    break;
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
            try { await warm.ConfigureAwait(false); } catch { /* best effort */ }

            // Continuous engine: a dedicated lister keeps polling the source
            // and feeding a live scored queue WHILE the workers transfer in parallel.
            // No list→drain→list barrier — a file that lands mid-transfer starts moving
            // the moment a slot frees up.
            var pending = new List<RaceFile>();
            var inFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sync = new object();
            var raceDone = 0; // 0 running, 1 complete, 2 stopped idle
            var sentCount = 0; // files WE actually moved (transferred also holds opponents' files)
            using var stopWorkers = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Streams are capped at wantSlots; when the pools have login headroom we run
            // extra workers that pre-negotiate (PRET/PASV/PORT) the NEXT files and sit at
            // this gate, firing STOR/RETR the instant a data slot frees.
            using var dataGate = new SemaphoreSlim(Math.Max(1, wantSlots), Math.Max(1, wantSlots));
            var stagingHeadroom = Math.Clamp(Math.Min(srcPool.Max - 1, dstPool.Max) - wantSlots, 0, 3);
            var workerCount = Math.Max(1, wantSlots + stagingHeadroom);
            // Wakes idle workers the instant the lister queues new files — no idle polling.
            using var workSignal = new SemaphoreSlim(0);
            // Files we must not retry before a given time (source still uploading them).
            var notBefore = new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

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

                // A listed target file means another racer/site already owns this name.
                // Even when the size is still growing, do not start a duplicate FXP: that
                // would burn bandwidth/credits only to lose on XDUPE or "upload in progress".
                return true;
            }

            void RecordSuccess(RaceFile f, DateTime startedAt)
            {
                transferred.TryAdd(f.Rel, true);
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
                    j.CumulativeBytes = cum;
                    j.SpeedBps = speed;
                });
                NotifyChangedThrottled();
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
                            files = await ListSourceFilesAsync(lister, req.SourcePath, skiplist, ct).ConfigureAwait(false);
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
                        int added = 0, pendingCount, inFlightCount;
                        lock (sync)
                        {
                            foreach (var f in files)
                            {
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
                            }
                            if (added > 0) SortPending();
                            pendingCount = pending.Count;
                            inFlightCount = inFlight.Count;
                        }
                        if (added > 0) workSignal.Release(Math.Min(added, Math.Max(1, wantSlots))); // wake idle workers NOW

                        if (files.Count != lastFound || poll % 30 == 1)
                        {
                            LogJobLive(id, "info", $"poll #{poll}: {files.Count} on source, {pendingCount} queued, {inFlightCount} in flight, {transferred.Count} done");
                            lastFound = files.Count;
                        }
                        // Refresh the speed each poll too, so it decays toward 0 when the
                        // source goes quiet instead of freezing at the last transfer's rate.
                        var liveSpeed = CurrentSpeed(DateTime.UtcNow);
                        _store.UpdateJobTransient(id, j =>
                        {
                            j.FilesTotal = Math.Max(j.FilesTotal, transferred.Count + pendingCount + inFlightCount);
                            j.SpeedBps = liveSpeed;
                        });

                        if (added == 0 && pendingCount == 0 && inFlightCount == 0)
                        {
                            idleCycles++;
                            // Completion probe over a BORROWED pooled connection (no
                            // dial+TLS+login per probe). SFV contents are the primary
                            // signal; complete markers remain the fallback for dirs
                            // without an SFV (zips, mp3 subdirs, ...).
                            var complete = false;
                            var probe = await dstPool.TryBorrowAsync(ct).ConfigureAwait(false);
                            if (probe is not null)
                            {
                                try
                                {
                                    var chk = await CheckReleaseOnAsync(probe, dstSite, req.DestPath, persist: false, ct).ConfigureAwait(false);
                                    dstPool.Return(probe);
                                    complete = chk.State == ReleaseState.Complete;
                                    if (complete)
                                        LogJob(id, "info", $"race complete ({chk.Description})");
                                }
                                catch (OperationCanceledException) { dstPool.Drop(probe); throw; }
                                catch (Exception ex)
                                {
                                    dstPool.Drop(probe);
                                    var m = FirstLineOf(ex.Message);
                                    if (!m.Contains("no such file", StringComparison.OrdinalIgnoreCase) &&
                                        !m.Contains("not found", StringComparison.OrdinalIgnoreCase))
                                        LogJobLive(id, "warn", $"completion check failed: {m}");
                                }
                            }
                            if (complete)
                            {
                                Volatile.Write(ref raceDone, 1);
                                break;
                            }
                            if (idleCycles >= maxIdle)
                            {
                                LogJob(id, "info", $"race stopped after {idleCycles} idle cycles with no new files");
                                Volatile.Write(ref raceDone, 2);
                                break;
                            }
                        }
                        else idleCycles = 0;

                        // Hot (files moving or just appeared): hammer the source like
                        // New pieces can land every few hundred ms.
                        // Quiet: back off gradually so we don't pound an idle dir.
                        var delay = added > 0 ? 100
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
                        try { await workSignal.WaitAsync(200, wct).ConfigureAwait(false); }
                        catch (OperationCanceledException) { return; }
                        continue;
                    }
                    var f = picked.Value;

                    if (DestinationAlreadyHas(f, out var existingSize))
                    {
                        transferred.TryAdd(f.Rel, true);
                        _store.UpdateJobTransient(id, j =>
                        {
                            var row = new FileTransfer
                            {
                                Name = f.Rel,
                                Size = Math.Max(0, f.Size),
                                StartedAt = DateTime.UtcNow,
                                Seconds = 0.001,
                                Status = "dupe",
                                Error = existingSize > 0
                                    ? $"already on destination ({HumanBytes(existingSize)})"
                                    : "already on destination"
                            };
                            j.Files.Add(row);
                            j.CurrentFile = f.Name;
                            if (j.Files.Count > 800) j.Files.RemoveRange(0, j.Files.Count - 800);
                        });
                        LogJobLive(id, "info", $"skipped {f.Rel}: already on destination{(existingSize > 0 ? $" ({HumanBytes(existingSize)})" : "")}");
                        FinishFile(f, requeue: false);
                        NotifyChangedThrottled();
                        continue;
                    }

                    // Borrow src+dst from the SHARED pools; if busy (another race), put the
                    // file back and wait briefly so the busy race keeps its throughput.
                    // A borrow that THROWS (dial refused, 530 too many connections, …) is
                    // retried the same way — it must never crash the worker, because a
                    // crashed worker used to fail the entire race job.
                    FtpClient? s = null, d = null;
                    try
                    {
                        s = await srcPool.TryBorrowAsync(ct).ConfigureAwait(false);
                        if (s is not null) d = await dstPool.TryBorrowAsync(ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        if (s is not null) srcPool.Return(s);
                        FinishFile(f, requeue: false);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (s is not null) srcPool.Return(s);
                        FinishFile(f, requeue: true);
                        LogJobLive(id, "warn", $"connect failed: {FirstLineOf(ex.Message)} — retrying");
                        try { await Task.Delay(750, wct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                        continue;
                    }
                    if (s is null)
                    {
                        FinishFile(f, requeue: true);
                        try { await Task.Delay(200, wct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                        continue;
                    }
                    if (d is null)
                    {
                        srcPool.Return(s);
                        FinishFile(f, requeue: true);
                        try { await Task.Delay(200, wct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
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
                            var row = j.Files.LastOrDefault(x => x.Name == f.Rel && x.Status == "active");
                            if (row is null) { row = new FileTransfer { Name = f.Rel, Size = Math.Max(0, f.Size), StartedAt = xferStart }; j.Files.Add(row); }
                            if (status != "active")
                            {
                                row.Seconds = Math.Max(0.001, (DateTime.UtcNow - xferStart).TotalSeconds);
                                row.Bps = status == "done" ? row.Size / row.Seconds : 0;
                            }
                            row.Status = status;
                            row.Error = error;
                            if (j.Files.Count > 800) j.Files.RemoveRange(0, j.Files.Count - 800);
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
                        if (slowKBps > 0 && f.Size > 0)
                        {
                            // Slow-skip: FXP bytes don't pass through us, so enforce the
                            // minimum speed as a time budget (size/threshold + grace for
                            // setup/gate). Blown budget => ABOR both sides, move on.
                            var budget = TimeSpan.FromSeconds(Math.Max(15, f.Size / 1024.0 / slowKBps + 10));
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
                        FileRow("slow", $"aborted: below {slowKBps} KB/s ({FirstLineOf(ex.Message)})");
                        LogJobLive(id, "warn", $"slowskip {f.Name}: aborted, transfer was below {slowKBps} KB/s");
                    }
                    catch (Exception ex) when (FxpTransfer.IsBeingUploaded(ex))
                    {
                        // Still uploading on source — requeue but don't hammer it: retry
                        // no sooner than 400ms from now, so the slot serves other files.
                        requeue = true;
                        notBefore[f.Rel] = DateTime.UtcNow.AddMilliseconds(400);
                        FileRow("wait", "still uploading on source");
                        _ = ex;
                    }
                    catch (Exception ex) when (FxpTransfer.IsSkippableTransferError(ex))
                    {
                        transferred.TryAdd(f.Rel, true); // already on dest / -missing / dupe
                        // X-DUPE replies list OTHER files already on the dest in this dir —
                        // learn the whole batch from one refusal instead of paying a failed
                        // STOR round trip for each.
                        var learned = 0;
                        foreach (var dupeName in FxpTransfer.ParseXdupeNames(ex))
                        {
                            var rel = string.IsNullOrEmpty(f.ParentRel) ? dupeName : f.ParentRel + "/" + dupeName;
                            destinationFiles.TryAdd(rel, -1);
                            if (transferred.TryAdd(rel, true)) learned++;
                        }
                        FileRow("dupe", FirstLineOf(ex.Message));
                        LogJobLive(id, "info", $"skipped {f.Name}: dupe{(learned > 0 ? $" (+{learned} more learned via X-DUPE)" : "")}");
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
                        if (srcOk) srcPool.Return(s); else srcPool.Drop(s);
                        if (dstOk) dstPool.Return(d); else dstPool.Drop(d);
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
                            var have = await ListSourceFilesAsync(conn, req.DestPath, skiplist, ct).ConfigureAwait(false);
                            dstPool.Return(conn);
                            lock (sync)
                            {
                                var newlyOwned = 0;
                                foreach (var h in have)
                                {
                                    destinationFiles[h.Rel] = h.Size;
                                    if (!inFlight.Contains(h.Rel) && transferred.TryAdd(h.Rel, true)) newlyOwned++;
                                }
                                if (newlyOwned > 0)
                                    pending.RemoveAll(p => transferred.ContainsKey(p.Rel));
                            }
                        }
                        catch (OperationCanceledException) { dstPool.Drop(conn); return; }
                        catch { dstPool.Drop(conn); } // dest dir may not exist yet — fine
                    }
                    try { await Task.Delay(Math.Max(pollMs, 500), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }

            var listerTask = ListerAsync();
            var workerTasks = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToList();
            workerTasks.Add(listerTask);
            workerTasks.Add(DestListerAsync());
            await Task.WhenAll(workerTasks).ConfigureAwait(false);
            return Volatile.Read(ref raceDone) == 1;
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
            if (!_pools.TryGetValue(name, out var pool))
            {
                var max = site.LoginSlots > 1 ? site.LoginSlots : Math.Max(3, Math.Max(site.DownloadSlots, site.UploadSlots));
                max = Math.Clamp(max + 1, 2, 40); // +1 headroom so the race lister never starves the transfer slots
                pool = new SitePool(cfg, max);
                _pools[name] = pool;
            }
            _poolRefs[name] = (_poolRefs.TryGetValue(name, out var n) ? n : 0) + 1;
            return pool;
        }
    }

    private void ReleasePool(string name)
    {
        SitePool? drop = null;
        lock (_poolLock)
        {
            if (!_poolRefs.TryGetValue(name, out var n)) return;
            n--;
            if (n <= 0)
            {
                _poolRefs.Remove(name);
                if (_pools.TryGetValue(name, out drop)) _pools.Remove(name);
            }
            else _poolRefs[name] = n;
        }
        drop?.DisposeAll();
    }

    // A capped pool of warm FTP connections to one site, shared by all races that use
    // that site. The semaphore caps concurrent in-use connections at the site's login
    // limit; idle connections are kept warm for reuse (no re-login churn).
    private sealed class SitePool
    {
        public FtpClient.Config Cfg { get; }
        private readonly SemaphoreSlim _gate;
        private readonly ConcurrentBag<(FtpClient Client, DateTime ReturnedUtc)> _idle = new();
        private static readonly TimeSpan IdleValidateAfter = TimeSpan.FromSeconds(15);

        public int Max { get; }

        public SitePool(FtpClient.Config cfg, int max)
        {
            Cfg = cfg;
            Max = Math.Max(1, max);
            _gate = new SemaphoreSlim(Max, Max);
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

        private async Task<FtpClient> TakeOrOpenAsync(CancellationToken ct)
        {
            // Reuse warm logged-in connections; NOOP-validate ones that sat idle long
            // enough for the server to have possibly dropped them, discard the dead.
            while (_idle.TryTake(out var warm))
            {
                if (DateTime.UtcNow - warm.ReturnedUtc < IdleValidateAfter) return warm.Client;
                if (await warm.Client.TryNoopAsync().ConfigureAwait(false)) return warm.Client;
                try { warm.Client.Dispose(); } catch { }
            }
            try
            {
                var c = await FtpClient.DialAndLoginAsync(Cfg, ct).ConfigureAwait(false);
                if (Cfg.UseXdupe) { try { await c.MaybeXdupeAsync().ConfigureAwait(false); } catch { } }
                return c;
            }
            catch { _gate.Release(); throw; } // couldn't open — give the permit back
        }

        // Open up to `count` connections concurrently and park them as idle, so the
        // first transfers don't pay dial+TLS+login latency one at a time.
        public async Task WarmUpAsync(int count, CancellationToken ct)
        {
            var need = Math.Min(count, _gate.CurrentCount);
            if (need <= 0) return;
            var dials = Enumerable.Range(0, need).Select(async _ =>
            {
                if (!_gate.Wait(0)) return;
                try
                {
                    var c = await FtpClient.DialAndLoginAsync(Cfg, ct).ConfigureAwait(false);
                    if (Cfg.UseXdupe) { try { await c.MaybeXdupeAsync().ConfigureAwait(false); } catch { } }
                    Return(c);
                }
                catch { _gate.Release(); }
            });
            await Task.WhenAll(dials).ConfigureAwait(false);
        }

        public void Return(FtpClient c) { _idle.Add((c, DateTime.UtcNow)); _gate.Release(); }
        public void Drop(FtpClient c) { try { c.Dispose(); } catch { } _gate.Release(); }
        public void DisposeAll() { while (_idle.TryTake(out var e)) { try { e.Client.Dispose(); } catch { } } }
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
        var segments = new List<string> { destRoot };
        if (!string.IsNullOrEmpty(relParent))
            foreach (var seg in relParent.Split('/', StringSplitOptions.RemoveEmptyEntries))
                segments.Add(seg);
        var path = "";
        foreach (var seg in segments)
        {
            path = path.Length == 0 ? seg : FtpClient.JoinRemote(path, seg);
            if (!made.Add(path)) continue;
            var (code, _) = await dst.CommandAsync("MKD " + path).ConfigureAwait(false);
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

    private async Task<bool> ReleaseCompleteAfterTransferErrorAsync(string id, TransferRequest req, Exception error)
    {
        if (string.IsNullOrWhiteSpace(req.ToSite) || string.IsNullOrWhiteSpace(req.DestPath))
            return false;

        try
        {
            var check = await CheckReleaseAsync(req.ToSite, req.DestPath, CancellationToken.None).ConfigureAwait(false);
            if (check.State != ReleaseState.Complete) return false;

            LogJob(id, "warn", $"transfer error ignored because destination is complete: {error.Message}");
            LogJob(id, "info", $"destination complete after transfer error: {req.ToSite}:{req.DestPath} ({check.Description})");
            FinishJob(id, null);
            return true;
        }
        catch (Exception checkEx) when (checkEx is not OperationCanceledException)
        {
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
            },
            CreatedAt = now,
            Events = { new JobEvent { Time = now, Level = "info", Message = "download queued" } },
        };
        var saved = _store.UpsertJob(job);
        Log("transfer", req.Site + " > local", "info", $"queued download {req.SourcePath} -> {req.DestPath}");
        ArmJobWatchdog(saved.Id);
        _ = Task.Run(() => RunDownloadJobAsync(saved.Id, req));
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

    private static int ResolveDownloadSlots(Site site)
    {
        var slots = site.DownloadSlots > 1 ? site.DownloadSlots : 3;
        if (site.LoginSlots > 1) slots = Math.Min(slots, site.LoginSlots);
        return Math.Clamp(slots, 1, 8);
    }

    private async Task RunDownloadJobAsync(string id, DownloadRequest req)
    {
        LogJob(id, "info", "download started");
        _store.UpdateJob(id, j => { j.State = JobState.Running; j.StartedAt = DateTime.UtcNow; });
        NotifyChanged();
        var ct = RegisterJobToken(id);
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
                FinishJob(id, null);
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
            FinishJob(id, null);
        }
        catch (OperationCanceledException)
        {
            // Stopped by the user — CancelJobInternal already marked the job.
            _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
            UnregisterJobToken(id);
            ClearProgress(id);
            NotifyChanged();
        }
        catch (Exception ex)
        {
            _store.UpdateJobTransient(id, j => j.Slots = new List<SlotProgress>());
            FinishJob(id, ex);
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

    // ---- job control (stop / pause) ----------------------------------------------------

    // Stop: per-job CancellationTokenSource — cancelling actually aborts the running
    // transfer work, not just the job row. Pause: workers finish the file in flight,
    // then hold before picking up the next one.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCts = new();
    private readonly ConcurrentDictionary<string, bool> _jobPaused = new();

    private CancellationToken RegisterJobToken(string id)
    {
        var cts = new CancellationTokenSource();
        _jobCts[id] = cts;
        return cts.Token;
    }

    private void ArmJobWatchdog(string id)
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
                    var job = _store.Job(id);
                    if (job is null || job.Terminal) return;
                    if (job.Paused) continue;

                    var lastActivity = job.HeartbeatAt != default
                        ? job.HeartbeatAt
                        : job.StartedAt != default ? job.StartedAt : job.CreatedAt;
                    if (lastActivity == default || DateTime.UtcNow - lastActivity < timeout) continue;

                    var reason = $"job watchdog timeout after {timeout.TotalMinutes:0} minute(s) without activity";
                    if (_jobCts.TryGetValue(id, out var cts))
                    {
                        try { cts.Cancel(); } catch { }
                    }
                    var failed = _store.FailJobIfStillRunning(id, reason);
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

    private void UnregisterJobToken(string id)
    {
        _jobPaused.TryRemove(id, out _);
        if (_jobCts.TryRemove(id, out var cts)) { try { cts.Dispose(); } catch { } }
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
            if (j.Events.Count > 3000)
                j.Events = j.Events.Skip(j.Events.Count - 3000).ToList();
        });
        var route = job is null ? id : job.Request.FromSite + " > " + job.Request.ToSite;
        Log("transfer", route, level, message); // Log() already throttles UI notify
    }

    private void FinishJob(string id, Exception? error)
    {
        UnregisterJobToken(id);
        var job = _store.UpdateJob(id, j =>
        {
            if (j.Terminal) return; // already cancelled/stopped — don't overwrite
            j.FinishedAt = DateTime.UtcNow;
            j.Paused = false;
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
        });
        ClearProgress(id);
        if (job is not null)
        {
            var route = job.Request.FromSite + " > " + job.Request.ToSite;
            if (error is not null) Log("transfer", route, "error", "job failed: " + error.Message);
            else Log("transfer", route, "info", $"job {job.Id} finished: {job.State.ToString().ToLowerInvariant()}");
        }
        NotifyChanged();
    }

    private void CancelJobInternal(string id, string reason)
    {
        // Actually abort the running work, not just flip the row's state.
        if (_jobCts.TryGetValue(id, out var cts)) { try { cts.Cancel(); } catch { } }
        _jobPaused.TryRemove(id, out _);
        var job = _store.UpdateJob(id, j =>
        {
            if (j.Terminal) return;
            j.State = JobState.Cancelled;
            j.FinishedAt = DateTime.UtcNow;
            j.Paused = false;
            j.Error = reason;
            j.Events.Add(new JobEvent { Time = DateTime.UtcNow, Level = "warn", Message = reason });
        });
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
