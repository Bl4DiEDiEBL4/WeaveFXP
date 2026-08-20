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
    private readonly string _path;
    private readonly object _lock = new();
    private State _state = new();

    // Best-effort flush when the process exits normally (Ctrl+C / service stop).
    private void HookProcessExit()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            lock (_lock) { if (_dirty) { try { FlushLocked(); } catch { } } }
        };
        Console.CancelKeyPress += (_, _) =>
        {
            lock (_lock) { if (_dirty) { try { FlushLocked(); } catch { } } }
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

    public JsonStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("state path is required");
        _path = path;
        HookProcessExit();
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            try
            {
                var data = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(data))
                {
                    var loaded = JsonSerializer.Deserialize<State>(data, JsonOptions);
                    if (loaded is not null) _state = loaded;
                }
            }
            catch (Exception ex)
            {
                LoadWarning = $"State file could not be loaded, started with defaults: {ex.Message}";
            }
        }
        Ensure();
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
    }

    // Debounced persistence. Every "save" used to serialize and rewrite the WHOLE
    // state.json synchronously — during a race that meant a multi-MB file rewritten
    // several times per second (100+ MB/s of disk writes). Now a save just marks the
    // state dirty; a background timer flushes it to disk at most every 2 seconds.
    private bool _dirty;
    private Timer? _flushTimer;

    // Memory-first persistence: normal saves only mark the state dirty and a
    // background timer writes it out every 30 seconds (plus a flush on process
    // exit). Losing <30s of job HISTORY on a hard crash is fine; configuration
    // (sites/settings) passes critical: true and always hits disk immediately.
    private void SaveLocked(bool critical = false)
    {
        _dirty = true;
        if (critical)
        {
            FlushLocked();
            return;
        }
        _flushTimer ??= new Timer(_ =>
        {
            lock (_lock)
            {
                if (_dirty) { try { FlushLocked(); } catch { } }
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    private void FlushLocked()
    {
        Ensure();
        _dirty = false;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, JsonOptions) + "\n");
        File.Move(tmp, _path, overwrite: true);
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
            SaveLocked(critical: true);
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
            SaveLocked();
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
            SaveLocked(critical: true);
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
            if (removed) SaveLocked(critical: true);
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
            InvalidateJobsSnapshotLocked();
            SaveLocked();
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
            if (job.Events.Count > 3000)
                job.Events = job.Events.Skip(job.Events.Count - 3000).ToList();
            if (job.Terminal)
            {
                // Finished: keep a useful tail, not megabytes per job forever.
                if (job.Events.Count > 400) job.Events = job.Events.Skip(job.Events.Count - 400).ToList();
                if (job.Files.Count > 400) job.Files = job.Files.Skip(job.Files.Count - 400).ToList();
                job.Slots = new List<SlotProgress>();
            }
            PruneJobsLocked();
            _state.Jobs[id] = job;
            InvalidateJobsSnapshotLocked();
            SaveLocked();
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
            if (job.Events.Count > 400) job.Events = job.Events.Skip(job.Events.Count - 400).ToList();
            _state.Jobs[id] = job;
            InvalidateJobsSnapshotLocked();
            SaveLocked();
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
                if (job.Events.Count > 400) job.Events = job.Events.Skip(job.Events.Count - 400).ToList();
                count++;
            }
            if (count > 0)
            {
                InvalidateJobsSnapshotLocked();
                SaveLocked();
            }
            return count;
        }
    }

    public Job? Job(string id)
    {
        lock (_lock)
            return _state.Jobs.TryGetValue(id, out var j) ? Clone(j) : null;
    }

    // Cloning every job (JSON round-trip, thousands of events each) for every UI
    // refresh was an allocation firehose — with several pages open it churned
    // gigabytes per minute. Serve a snapshot, rebuilt at most every 250ms.
    private List<Job>? _jobsSnapshot;
    private DateTime _jobsSnapshotAt;

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
            _jobsSnapshot = _state.Jobs.Values.OrderByDescending(j => j.CreatedAt).Select(Clone).ToList();
            _jobsSnapshotAt = DateTime.UtcNow;
            return _jobsSnapshot;
        }
    }

    // Keep the stored job set bounded: completed jobs beyond the cap are dropped
    // oldest-first (running/queued jobs are never pruned).
    private void PruneJobsLocked()
    {
        const int MaxJobs = 150;
        if (_state.Jobs.Count <= MaxJobs) return;
        var doomed = _state.Jobs.Values
            .Where(j => j.Terminal)
            .OrderBy(j => j.CreatedAt)
            .Take(_state.Jobs.Count - MaxJobs)
            .Select(j => j.Id)
            .ToList();
        foreach (var id in doomed) _state.Jobs.Remove(id);
    }

    public int ClearJobs()
    {
        lock (_lock)
        {
            Ensure();
            var count = _state.Jobs.Count;
            if (count == 0) return 0;
            _state.Jobs.Clear();
            InvalidateJobsSnapshotLocked();
            SaveLocked();
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
            if (!_state.Jobs.Remove(id)) return false;
            InvalidateJobsSnapshotLocked();
            SaveLocked();
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
            SaveLocked();
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
                SaveLocked();
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
            SaveLocked();
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
            SaveLocked();
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
            SaveLocked();
            return count;
        }
    }

    private static string ReleaseKey(string site, string path) => Key(site + ":" + path);

    // Deep clone via round-trip so callers never mutate stored objects.
    private static T Clone<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, JsonOptions), JsonOptions)!;

    private static Job CloneJobHeader(Job job) => new()
    {
        Id = job.Id,
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
