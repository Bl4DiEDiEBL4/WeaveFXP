using System.Text.Json;
using System.Text.Json.Serialization;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

// Persisted state file (data/state.json).
public sealed class State
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new AppSettings().WithDefaults();
    public Dictionary<string, Site> Sites { get; set; } = new();
    public Dictionary<string, Job> Jobs { get; set; } = new();
    public Dictionary<string, ReleaseCheck> Releases { get; set; } = new();
    public List<DupeResult> Dupes { get; set; } = new();
    // Per-site hourly traffic buckets (out = sent from the site, in = received to it).
    public List<SiteHourStat> SiteStats { get; set; } = new();
}

/// <summary>
/// Thread-safe JSON-file store.
/// </summary>
public sealed class JsonStore
{
    [Flags]
    private enum StateSections
    {
        None = 0,
        Settings = 1,
        Sites = 2,
        Jobs = 4,
        Releases = 8,
        Dupes = 16,
        Stats = 32,
        All = Settings | Sites | Jobs | Releases | Dupes | Stats,
    }

    private readonly string _path;
    private readonly string _dir;
    private readonly string _settingsPath;
    private readonly string _sitesPath;
    private readonly string _jobsPath;
    private readonly string _releasesPath;
    private readonly string _dupesPath;
    private readonly string _statsPath;
    private readonly string _backupDir;
    private readonly JobArchiveStore _jobArchive;
    private readonly object _lock = new();
    private State _state = new();

    // Best-effort flush when the process exits normally (Ctrl+C / service stop).
    private void HookProcessExit()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (_lock) { if (_dirtySections != StateSections.None) { try { FlushLocked(); } catch { } } }
        };
        Console.CancelKeyPress += (_, _) =>
        {
            lock (_lock) { if (_dirtySections != StateSections.None) { try { FlushLocked(); } catch { } } }
        };
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        Converters =
        {
            new FlexibleEnumConverter<TlsMode>(),
            new FlexibleEnumConverter<FxpMode>(),
            new FlexibleEnumConverter<TransferProtocol>(),
            new FlexibleEnumConverter<ApiListenMode>(),
            new FlexibleEnumConverter<JobType>(),
            new FlexibleEnumConverter<JobState>(),
            new FlexibleEnumConverter<ReleaseState>(),
        },
    };

    private static readonly JsonSerializerOptions CompactJsonOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    public JsonStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("state path is required");
        _path = path;
        _dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        _settingsPath = System.IO.Path.Combine(_dir, "settings.json");
        _sitesPath = System.IO.Path.Combine(_dir, "sites.json");
        _jobsPath = System.IO.Path.Combine(_dir, "jobs.json");
        _releasesPath = System.IO.Path.Combine(_dir, "releases.json");
        _dupesPath = System.IO.Path.Combine(_dir, "dupes.json");
        _statsPath = System.IO.Path.Combine(_dir, "stats.json");
        _backupDir = System.IO.Path.Combine(_dir, "backup");
        HookProcessExit();
        Directory.CreateDirectory(_dir);
        _jobArchive = new JobArchiveStore(System.IO.Path.Combine(_dir, "history.db"));

        LoadStateFromDisk();
        Ensure();
        // Cull an oversized history from a previous run immediately, so a huge jobs.json
        // doesn't sit in memory (and get cloned every UI tick) until the next update.
        lock (_lock)
        {
            if (PruneJobsLocked() > 0)
                SaveLocked(StateSections.Jobs, critical: true);
        }
    }

    public string Path => _path;
    public string LoadWarning { get; private set; } = "";

    private void Ensure()
    {
        if (_state.Version == 0) _state.Version = 1;
        _state.Settings = (_state.Settings ?? new AppSettings()).WithDefaults();
        _state.Sites ??= new();
        _state.Jobs ??= new();
        _state.Releases ??= new();
        _state.Dupes ??= new();
        _state.SiteStats ??= new();
    }

    // Debounced persistence. Every "save" used to serialize and rewrite state.json
    // synchronously; during a race that meant a multi-MB file rewritten several times
    // per second. Now saves mark only the touched state sections dirty, and a
    // background timer flushes runtime data at most every 30 seconds.
    private StateSections _dirtySections = StateSections.None;
    private Timer? _flushTimer;

    // Memory-first persistence: normal saves only mark the state dirty and a
    // background timer writes it out every 30 seconds (plus a flush on process
    // exit). Losing <30s of job HISTORY on a hard crash is fine; critical
    // configuration writes can still force their own section to disk immediately.
    private void SaveLocked(StateSections sections = StateSections.All, bool critical = false)
    {
        _dirtySections |= sections;
        if (critical)
        {
            FlushLocked(sections);
            return;
        }
        _flushTimer ??= new Timer(_ =>
        {
            lock (_lock)
            {
                if (_dirtySections != StateSections.None) { try { FlushLocked(); } catch { } }
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void FlushLocked(StateSections sections = StateSections.All)
    {
        Ensure();
        Directory.CreateDirectory(_dir);

        var toFlush = _dirtySections & sections;
        if (toFlush == StateSections.None) return;

        if (toFlush.HasFlag(StateSections.Settings)) WriteJsonDurable(_settingsPath, _state.Settings, _backupDir);
        if (toFlush.HasFlag(StateSections.Sites)) WriteJsonDurable(_sitesPath, _state.Sites, _backupDir);
        if (toFlush.HasFlag(StateSections.Jobs)) WriteJsonDurable(_jobsPath, _state.Jobs, _backupDir);
        if (toFlush.HasFlag(StateSections.Releases)) WriteJsonDurable(_releasesPath, _state.Releases, _backupDir);
        if (toFlush.HasFlag(StateSections.Dupes)) WriteJsonDurable(_dupesPath, _state.Dupes, _backupDir);
        if (toFlush.HasFlag(StateSections.Stats)) WriteJsonDurable(_statsPath, _state.SiteStats, _backupDir);

        _dirtySections &= ~toFlush;
    }

    private void LoadStateFromDisk()
    {
        var loadedSplit = LoadSplitState();
        if (loadedSplit)
            return;

        if (TryLoadLegacyState(out var source, out var errors))
        {
            if (!source.Equals(_path, StringComparison.OrdinalIgnoreCase))
            {
                LoadWarning = $"Recovered state from {System.IO.Path.GetFileName(source)} because state.json was not readable.";
                try { WriteSplitStateSnapshot(); } catch { }
            }
            return;
        }

        if (errors.Count > 0)
            LoadWarning = "State file could not be loaded, started with defaults: " + string.Join("; ", errors.Take(3));
    }

    private bool TryLoadLegacyState(out string source, out List<string> errors)
    {
        source = "";
        errors = new List<string>();
        var candidates = new[]
        {
            _path,
            BackupPath(_path, _dir, 1),
            BackupPath(_path, _dir, 2),
            _path + ".bak",
            _path + ".bak2",
            _path + ".tmp",
        };

        foreach (var candidate in candidates)
        {
            if (!TryLoadStateFile(candidate, out var loaded, out var error))
            {
                if (!string.IsNullOrWhiteSpace(error)) errors.Add(error);
                continue;
            }

            _state = loaded!;
            source = candidate;
            return true;
        }
        return false;
    }

    private bool LoadSplitState()
    {
        var anySplitFile = new[] { _settingsPath, _sitesPath, _jobsPath, _releasesPath, _dupesPath, _statsPath }
            .Any(File.Exists);
        if (!anySplitFile) return false;

        // A first split-state write can be interrupted after only one section. Seed
        // missing/corrupt sections from the legacy monolithic state before overlaying
        // every split file that is available, so a partial migration never resets data.
        var loadedLegacy = TryLoadLegacyState(out var legacySource, out _);
        var recovered = new List<string>();
        var errors = new List<string>();
        if (TryLoadJsonFile<AppSettings>(_settingsPath, out var settings, out var settingsSource, out var settingsError))
        {
            _state.Settings = settings!;
            if (!settingsSource.Equals(_settingsPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(settingsSource);
        }
        else if (!string.IsNullOrWhiteSpace(settingsError)) errors.Add(settingsError);

        if (TryLoadJsonFile<Dictionary<string, Site>>(_sitesPath, out var sites, out var sitesSource, out var sitesError))
        {
            _state.Sites = sites!;
            if (!sitesSource.Equals(_sitesPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(sitesSource);
        }
        else if (!string.IsNullOrWhiteSpace(sitesError)) errors.Add(sitesError);

        if (TryLoadJsonFile<Dictionary<string, Job>>(_jobsPath, out var jobs, out var jobsSource, out var jobsError))
        {
            _state.Jobs = jobs!;
            if (!jobsSource.Equals(_jobsPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(jobsSource);
        }
        else if (!string.IsNullOrWhiteSpace(jobsError)) errors.Add(jobsError);

        if (TryLoadJsonFile<Dictionary<string, ReleaseCheck>>(_releasesPath, out var releases, out var releasesSource, out var releasesError))
        {
            _state.Releases = releases!;
            if (!releasesSource.Equals(_releasesPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(releasesSource);
        }
        else if (!string.IsNullOrWhiteSpace(releasesError)) errors.Add(releasesError);

        if (TryLoadJsonFile<List<DupeResult>>(_dupesPath, out var dupes, out var dupesSource, out var dupesError))
        {
            _state.Dupes = dupes!;
            if (!dupesSource.Equals(_dupesPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(dupesSource);
        }
        else if (!string.IsNullOrWhiteSpace(dupesError)) errors.Add(dupesError);

        if (TryLoadJsonFile<List<SiteHourStat>>(_statsPath, out var stats, out var statsSource, out var statsError))
        {
            _state.SiteStats = stats!;
            if (!statsSource.Equals(_statsPath, StringComparison.OrdinalIgnoreCase)) recovered.Add(statsSource);
        }
        else if (!string.IsNullOrWhiteSpace(statsError)) errors.Add(statsError);

        if (recovered.Count > 0)
            LoadWarning = "Recovered state section(s) from backup: " + string.Join(", ", recovered.Select(System.IO.Path.GetFileName));
        else if (errors.Count > 0)
            LoadWarning = "Some state section(s) could not be loaded: " + string.Join("; ", errors.Take(3));
        else if (loadedLegacy && !legacySource.Equals(_path, StringComparison.OrdinalIgnoreCase))
            LoadWarning = $"Used {System.IO.Path.GetFileName(legacySource)} as fallback while loading split state.";
        return true;
    }

    private void WriteSplitStateSnapshot()
    {
        Ensure();
        Directory.CreateDirectory(_dir);
        WriteJsonDurable(_settingsPath, _state.Settings, _backupDir);
        WriteJsonDurable(_sitesPath, _state.Sites, _backupDir);
        WriteJsonDurable(_jobsPath, _state.Jobs, _backupDir);
        WriteJsonDurable(_releasesPath, _state.Releases, _backupDir);
        WriteJsonDurable(_dupesPath, _state.Dupes, _backupDir);
        WriteJsonDurable(_statsPath, _state.SiteStats, _backupDir);
    }

    private static bool TryLoadStateFile(string path, out State? state, out string error)
    {
        state = null;
        error = "";
        if (!File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length == 0)
            {
                error = $"{System.IO.Path.GetFileName(path)} is empty";
                return false;
            }

            var data = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(data))
            {
                error = $"{System.IO.Path.GetFileName(path)} only contains whitespace";
                return false;
            }

            var loaded = JsonSerializer.Deserialize<State>(data, JsonOptions);
            if (loaded is null)
            {
                error = $"{System.IO.Path.GetFileName(path)} deserialized to null";
                return false;
            }

            state = loaded;
            return true;
        }
        catch (Exception ex)
        {
            error = $"{System.IO.Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
    }

    private static bool TryLoadJsonFile<T>(string path, out T? value, out string source, out string error)
    {
        value = default;
        source = "";
        error = "";
        foreach (var candidate in StateFileCandidates(path))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                var info = new FileInfo(candidate);
                if (info.Length == 0)
                {
                    error = $"{System.IO.Path.GetFileName(candidate)} is empty";
                    continue;
                }

                var data = File.ReadAllText(candidate);
                if (string.IsNullOrWhiteSpace(data))
                {
                    error = $"{System.IO.Path.GetFileName(candidate)} only contains whitespace";
                    continue;
                }

                var loaded = JsonSerializer.Deserialize<T>(data, JsonOptions);
                if (loaded is null)
                {
                    error = $"{System.IO.Path.GetFileName(candidate)} deserialized to null";
                    continue;
                }

                value = loaded;
                source = candidate;
                return true;
            }
            catch (Exception ex)
            {
                error = $"{System.IO.Path.GetFileName(candidate)}: {ex.Message}";
            }
        }
        return false;
    }

    private static IEnumerable<string> StateFileCandidates(string path)
    {
        yield return path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? AppContext.BaseDirectory;
        yield return BackupPath(path, dir, 1);
        yield return BackupPath(path, dir, 2);
        yield return path + ".tmp";
    }

    private static void WriteJsonDurable<T>(string path, T value, string backupDir)
    {
        var json = JsonSerializer.Serialize(value, CompactJsonOptions) + "\n";
        if (string.IsNullOrWhiteSpace(json) || json.Length < 3)
            throw new IOException($"refusing to write empty state section {System.IO.Path.GetFileName(path)}");

        var tmp = path + ".tmp";
        var bak = BackupPath(path, System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path)) ?? AppContext.BaseDirectory, 1);
        WriteAllTextDurable(tmp, json);

        if (File.Exists(path))
        {
            Directory.CreateDirectory(backupDir);
            RotateBackup(bak);
            File.Replace(tmp, path, bak, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(tmp, path);
        }
    }

    private static void WriteAllTextDurable(string path, string data)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        fs.Write(bytes, 0, bytes.Length);
        fs.Flush(flushToDisk: true);
    }

    private static void RotateBackup(string bak)
    {
        try
        {
            var bak2 = bak + "2";
            if (File.Exists(bak2)) File.Delete(bak2);
            if (File.Exists(bak)) File.Move(bak, bak2);
        }
        catch
        {
            // Best effort only. File.Replace below will still create/replace .bak.
        }
    }

    private static string BackupPath(string path, string dataDir, int generation)
    {
        var name = System.IO.Path.GetFileName(path) + (generation <= 1 ? ".bak" : ".bak2");
        return System.IO.Path.Combine(dataDir, "backup", name);
    }

    private static string Key(string name) => (name ?? "").Trim().ToLowerInvariant();

    // ---- settings ----

    public AppSettings Settings()
    {
        lock (_lock) return Clone(_state.Settings);
    }

    public AppSettings UpdateSettings(AppSettings settings)
    {
        settings = settings.WithDefaults();
        settings.Validate();
        lock (_lock)
        {
            Ensure();
            if (settings.CreatedAt == default) settings.CreatedAt = _state.Settings.CreatedAt;
            _state.Settings = settings;
            SaveLocked(StateSections.Settings, critical: true);
            if (PruneJobsLocked() > 0) SaveLocked(StateSections.Jobs);
            return Clone(settings);
        }
    }

    // ---- sites ----

    public Site UpsertSite(Site site)
    {
        site = site.WithDefaults();
        site.Validate();
        lock (_lock)
        {
            Ensure();
            var k = Key(site.Name);
            if (_state.Sites.TryGetValue(k, out var existing) && existing.CreatedAt != default)
                site.CreatedAt = existing.CreatedAt;
            _state.Sites[k] = site;
            SaveLocked(StateSections.Sites);
            return Clone(site);
        }
    }

    public Site SaveSite(string? originalName, Site site)
    {
        site = site.WithDefaults();
        site.Validate();
        var oldKey = Key(originalName ?? "");
        var newKey = Key(site.Name);
        lock (_lock)
        {
            Ensure();

            if (oldKey.Length > 0 && oldKey != newKey)
            {
                if (_state.Sites.ContainsKey(newKey))
                    throw new ArgumentException($"site \"{site.Name}\" already exists");

                if (_state.Sites.TryGetValue(oldKey, out var existing))
                {
                    if (site.CreatedAt == default) site.CreatedAt = existing.CreatedAt;
                    _state.Sites.Remove(oldKey);

                    for (var i = 0; i < _state.Settings.SiteOrder.Count; i++)
                    {
                        if (_state.Settings.SiteOrder[i].Equals(originalName, StringComparison.OrdinalIgnoreCase))
                            _state.Settings.SiteOrder[i] = site.Name;
                    }
                }
            }
            else if (_state.Sites.TryGetValue(newKey, out var existing) && existing.CreatedAt != default)
            {
                site.CreatedAt = existing.CreatedAt;
            }

            _state.Sites[newKey] = site;
            SaveLocked(StateSections.Sites | StateSections.Settings, critical: true);
            return Clone(site);
        }
    }

    public bool DeleteSite(string name)
    {
        lock (_lock)
        {
            Ensure();
            var removed = _state.Sites.Remove(Key(name));
            if (removed)
                _state.Settings.SiteOrder.RemoveAll(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (removed) SaveLocked(StateSections.Sites | StateSections.Settings, critical: true);
            return removed;
        }
    }

    public Site? Site(string name)
    {
        lock (_lock)
            return _state.Sites.TryGetValue(Key(name), out var s) ? Clone(s) : null;
    }

    public List<Site> Sites()
    {
        lock (_lock)
            return _state.Sites.Values
                .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(Clone).ToList();
    }

    // ---- jobs ----

    public Job UpsertJob(Job job)
    {
        lock (_lock)
        {
            Ensure();
            if (job.State == JobState.Running && job.HeartbeatAt == default)
                job.HeartbeatAt = DateTime.UtcNow;
            _state.Jobs[job.Id] = job;
            PruneJobsLocked();
            InvalidateJobsSnapshotLocked();
            SaveLocked(StateSections.Jobs);
            return Clone(job);
        }
    }

    public Job? UpdateJob(string id, Action<Job> update)
    {
        lock (_lock)
        {
            Ensure();
            if (!_state.Jobs.TryGetValue(id, out var job)) return null;
            update(job);
            if (job.State == JobState.Running)
                job.HeartbeatAt = DateTime.UtcNow;
            if (job.Terminal)
            {
                // Keep the complete per-file and event history. The configured job-count
                // cap bounds total history; trimming here made the race detail view lie.
                job.Slots = new List<SlotProgress>();
            }
            PruneJobsLocked();
            _state.Jobs[id] = job;
            InvalidateJobsSnapshotLocked();
            SaveLocked(StateSections.Jobs);
            return Clone(job);
        }
    }

    // Mutate a job in memory WITHOUT writing state.json. Used for high-frequency
    // live progress (bytes/speed) so streaming a file doesn't hammer the disk.
    // The next persisting UpdateJob (e.g. on finish) writes the final values.
    public Job? UpdateJobTransient(string id, Action<Job> update)
    {
        lock (_lock)
        {
            if (!_state.Jobs.TryGetValue(id, out var job)) return null;
            update(job);
            if (job.State == JobState.Running)
                job.HeartbeatAt = DateTime.UtcNow;
            return CloneJobHeader(job);
        }
    }

    public Job? FailJobIfStillRunning(string id, string reason)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return null;

        lock (_lock)
        {
            Ensure();
            if (!_state.Jobs.TryGetValue(id, out var job) || job.State != JobState.Running) return null;
            var now = DateTime.UtcNow;
            job.State = JobState.Failed;
            job.FinishedAt = now;
            job.Paused = false;
            job.Error = reason;
            job.Slots = new List<SlotProgress>();
            job.SpeedBps = 0;
            job.Events.Add(new JobEvent { Time = now, Level = "error", Message = reason });
            _state.Jobs[id] = job;
            InvalidateJobsSnapshotLocked();
            SaveLocked(StateSections.Jobs);
            return Clone(job);
        }
    }

    public int FailInterruptedJobs(string reason)
    {
        lock (_lock)
        {
            Ensure();
            var now = DateTime.UtcNow;
            var count = 0;
            foreach (var job in _state.Jobs.Values.Where(j => j.State == JobState.Running).ToList())
            {
                job.State = JobState.Failed;
                job.FinishedAt = now;
                job.Paused = false;
                job.Error = reason;
                job.Slots = new List<SlotProgress>();
                job.SpeedBps = 0;
                job.Events.Add(new JobEvent { Time = now, Level = "error", Message = reason });
                count++;
            }
            if (count > 0)
            {
                InvalidateJobsSnapshotLocked();
                SaveLocked(StateSections.Jobs);
            }
            return count;
        }
    }

    public Job? Job(string id)
    {
        lock (_lock)
        {
            if (_state.Jobs.TryGetValue(id, out var j)) return Clone(j);
        }
        var archived = _jobArchive.Payload(id);
        return string.IsNullOrWhiteSpace(archived)
            ? null
            : JsonSerializer.Deserialize<Job>(archived, JsonOptions);
    }

    // Cloning every job (JSON round-trip, thousands of events each) for every UI
    // refresh was an allocation firehose — with several pages open it churned
    // gigabytes per minute. Serve a snapshot, rebuilt at most every 250ms.
    private List<Job>? _jobsSnapshot;
    private DateTime _jobsSnapshotAt;
    private readonly Dictionary<string, Job> _terminalJobSnapshots = new(StringComparer.OrdinalIgnoreCase);

    private void InvalidateJobsSnapshotLocked()
    {
        _jobsSnapshot = null;
        _jobsSnapshotAt = default;
    }

    public List<Job> Jobs()
    {
        lock (_lock)
        {
            if (_jobsSnapshot is not null && (DateTime.UtcNow - _jobsSnapshotAt).TotalMilliseconds < 250)
                return _jobsSnapshot;
            var liveIds = new HashSet<string>(_state.Jobs.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _terminalJobSnapshots.Keys.Where(id => !liveIds.Contains(id)).ToList())
                _terminalJobSnapshots.Remove(stale);

            _jobsSnapshot = _state.Jobs.Values
                .OrderByDescending(j => j.CreatedAt)
                .Select(j => SnapshotJobLocked(j))
                .ToList();
            _jobsSnapshotAt = DateTime.UtcNow;
            return _jobsSnapshot;
        }
    }

    private Job SnapshotJobLocked(Job job)
    {
        if (!job.Terminal) return Clone(job);
        if (_terminalJobSnapshots.TryGetValue(job.Id, out var cached) &&
            cached.State == job.State && cached.FinishedAt == job.FinishedAt &&
            cached.Events.Count == job.Events.Count && cached.Files.Count == job.Files.Count)
            return cached;

        var snapshot = Clone(job);
        _terminalJobSnapshots[job.Id] = snapshot;
        return snapshot;
    }

    public List<Job> HistoryJobs(int archiveLimit = 10000)
    {
        List<Job> result;
        lock (_lock)
            result = _state.Jobs.Values.Select(CloneJobHeader).ToList();

        var hotIds = new HashSet<string>(result.Select(j => j.Id), StringComparer.OrdinalIgnoreCase);
        result.AddRange(_jobArchive.Headers(archiveLimit).Where(j => !hotIds.Contains(j.Id)));
        return result.OrderByDescending(j => j.CreatedAt).ToList();
    }

    public int ArchivedJobCount() => _jobArchive.Count();

    public List<LogEntry> StoredLogs(int limit) => _jobArchive.RecentLogs(limit);

    public void AppendLogs(IReadOnlyCollection<LogEntry> entries, int maxEntries) =>
        _jobArchive.AppendLogs(entries, maxEntries);

    public int ClearStoredLogs() => _jobArchive.ClearLogs();

    // Keep the stored job set bounded: completed jobs beyond the cap are dropped
    // oldest-first (running/queued jobs are never pruned).
    private int PruneJobsLocked()
    {
        // JSON is the hot working set only. Older terminal jobs are transactionally
        // archived before removal, so full file/event history does not make jobs.json
        // grow forever or get rewritten on every periodic flush.
        var maxJobs = _state.Settings.StoredJobHistoryLimit <= 0
            ? 150
            : Math.Clamp(_state.Settings.StoredJobHistoryLimit, 25, 150);
        if (_state.Jobs.Count <= maxJobs) return 0;
        var doomed = _state.Jobs.Values
            .Where(j => j.Terminal)
            .OrderBy(j => j.CreatedAt)
            .Take(_state.Jobs.Count - maxJobs)
            .ToList();
        if (doomed.Count == 0) return 0;

        try
        {
            _jobArchive.Archive(doomed
                .Select(job => (job, JsonSerializer.Serialize(job, CompactJsonOptions)))
                .ToList());
        }
        catch (Exception ex)
        {
            LoadWarning = $"Job archive failed; jobs kept in JSON: {ex.Message}";
            return 0;
        }
        foreach (var job in doomed)
        {
            _state.Jobs.Remove(job.Id);
            _terminalJobSnapshots.Remove(job.Id);
        }
        InvalidateJobsSnapshotLocked();
        return doomed.Count;
    }

    public int ClearJobs()
    {
        lock (_lock)
        {
            Ensure();
            var terminalIds = _state.Jobs.Values
                .Where(job => job.Terminal)
                .Select(job => job.Id)
                .ToList();
            var count = terminalIds.Count + _jobArchive.Count();
            if (count == 0) return 0;
            foreach (var id in terminalIds)
            {
                _state.Jobs.Remove(id);
                _terminalJobSnapshots.Remove(id);
            }
            _jobArchive.Clear();
            if (terminalIds.Count > 0)
            {
                InvalidateJobsSnapshotLocked();
                SaveLocked(StateSections.Jobs);
            }
            return count;
        }
    }

    public bool DeleteJob(string id)
    {
        id = (id ?? "").Trim();
        if (id.Length == 0) return false;

        lock (_lock)
        {
            Ensure();
            var removedHot = _state.Jobs.Remove(id);
            var removedArchived = _jobArchive.Delete(id);
            if (!removedHot && !removedArchived) return false;
            if (removedHot)
            {
                _terminalJobSnapshots.Remove(id);
                InvalidateJobsSnapshotLocked();
                SaveLocked(StateSections.Jobs);
            }
            return true;
        }
    }

    // ---- releases / dupes ----

    public void UpsertRelease(ReleaseCheck check)
    {
        lock (_lock)
        {
            Ensure();
            _state.Releases[ReleaseKey(check.Site, check.Path)] = check;
            // Keep the release-check history bounded.
            if (_state.Releases.Count > 300)
            {
                foreach (var key in _state.Releases.OrderBy(kv => kv.Value.CheckedAt)
                             .Take(_state.Releases.Count - 300).Select(kv => kv.Key).ToList())
                    _state.Releases.Remove(key);
            }
            SaveLocked(StateSections.Releases);
        }
    }

    public List<ReleaseCheck> Releases()
    {
        lock (_lock)
            return _state.Releases.Values.OrderByDescending(r => r.CheckedAt).Select(Clone).ToList();
    }

    // ---- per-site traffic stats ---------------------------------------------------------

    private DateTime _lastStatsSave = DateTime.MinValue;

    public void AddSiteTraffic(string site, long outBytes, long inBytes, double seconds)
    {
        if (string.IsNullOrWhiteSpace(site) || (outBytes <= 0 && inBytes <= 0)) return;
        lock (_lock)
        {
            Ensure();
            var hour = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, DateTime.UtcNow.Day, DateTime.UtcNow.Hour, 0, 0, DateTimeKind.Utc);
            var bucket = _state.SiteStats.FirstOrDefault(b => b.HourUtc == hour && b.Site.Equals(site, StringComparison.OrdinalIgnoreCase));
            if (bucket is null)
            {
                bucket = new SiteHourStat { Site = site, HourUtc = hour };
                _state.SiteStats.Add(bucket);
                // Prune anything older than 60 days so state.json stays bounded.
                var cutoff = hour.AddDays(-60);
                _state.SiteStats.RemoveAll(b => b.HourUtc < cutoff);
            }
            bucket.OutBytes += Math.Max(0, outBytes);
            bucket.InBytes += Math.Max(0, inBytes);
            bucket.Files += 1;
            bucket.Seconds += Math.Max(0, seconds);
            // Stats arrive per completed file — persist at most every 5s to keep
            // busy races off the disk.
            if ((DateTime.UtcNow - _lastStatsSave).TotalSeconds >= 5)
            {
                _lastStatsSave = DateTime.UtcNow;
                SaveLocked(StateSections.Stats);
            }
        }
    }

    public List<SiteHourStat> SiteStats()
    {
        lock (_lock)
            return _state.SiteStats.Select(b => new SiteHourStat
            {
                Site = b.Site,
                HourUtc = b.HourUtc,
                OutBytes = b.OutBytes,
                InBytes = b.InBytes,
                Files = b.Files,
                Seconds = b.Seconds,
            }).ToList();
    }

    public int ClearReleases()
    {
        lock (_lock)
        {
            Ensure();
            var count = _state.Releases.Count;
            if (count == 0) return 0;
            _state.Releases.Clear();
            SaveLocked(StateSections.Releases);
            return count;
        }
    }

    public void AddDupe(DupeResult result)
    {
        lock (_lock)
        {
            Ensure();
            _state.Dupes.Add(result);
            if (_state.Dupes.Count > 500)
                _state.Dupes.RemoveRange(0, _state.Dupes.Count - 500);
            if (_state.Dupes.Count > 200)
                _state.Dupes = _state.Dupes.Skip(_state.Dupes.Count - 200).ToList();
            SaveLocked(StateSections.Dupes);
        }
    }

    public int DupeCount()
    {
        lock (_lock)
        {
            Ensure();
            return _state.Dupes.Count;
        }
    }

    public int ClearDupes()
    {
        lock (_lock)
        {
            Ensure();
            var count = _state.Dupes.Count;
            if (count == 0) return 0;
            _state.Dupes.Clear();
            SaveLocked(StateSections.Dupes);
            return count;
        }
    }

    private static string ReleaseKey(string site, string path) => Key(site + ":" + path);

    // Deep clone via round-trip so callers never mutate stored objects.
    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, CompactJsonOptions), JsonOptions)!;

    private static Job CloneJobHeader(Job job) => new()
    {
        Id = job.Id,
        BatchId = job.BatchId,
        Type = job.Type,
        State = job.State,
        Request = job.Request,
        CreatedAt = job.CreatedAt,
        StartedAt = job.StartedAt,
        FinishedAt = job.FinishedAt,
        Error = job.Error,
        Paused = job.Paused,
        BytesDone = job.BytesDone,
        BytesTotal = job.BytesTotal,
        CumulativeBytes = job.CumulativeBytes,
        SpeedBps = job.SpeedBps,
        FilesDone = job.FilesDone,
        FilesTotal = job.FilesTotal,
        CurrentFile = job.CurrentFile,
        HeartbeatAt = job.HeartbeatAt,
    };

    private sealed class FlexibleEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
    {
        public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var n))
                return (TEnum)Enum.ToObject(typeof(TEnum), n);

            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException($"Expected string for {typeof(TEnum).Name}");

            var value = reader.GetString() ?? "";
            var normalized = Normalize(value);
            foreach (var name in Enum.GetNames<TEnum>())
            {
                if (Normalize(name) == normalized)
                    return Enum.Parse<TEnum>(name);
            }

            throw new JsonException($"Unknown {typeof(TEnum).Name} value '{value}'");
        }

        public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
            => writer.WriteStringValue(ToSnake(value.ToString()));

        private static string Normalize(string value)
        {
            var chars = value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant);
            return new string(chars.ToArray());
        }

        private static string ToSnake(string value)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var prev = i > 0 ? value[i - 1] : '\0';
                var next = i < value.Length - 1 ? value[i + 1] : '\0';
                if (i > 0 && char.IsUpper(c) &&
                    (char.IsLower(prev) || char.IsDigit(prev) || (char.IsUpper(prev) && char.IsLower(next))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
