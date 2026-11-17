using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

// cbftp tracks in-flight FXP bytes from the destination FileList. These are
// observations, never transfer credits; accounting still requires both final replies.
internal sealed class RaceProgressPublisher
{
    private sealed class Entry
    {
        public required FileTransfer Row;
        public long? LastBytes;
        public DateTime LastAt;
    }
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _next = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonStore _store;
    private readonly string _jobId;

    public RaceProgressPublisher(JsonStore store, string jobId) { _store = store; _jobId = jobId; }

    public void Start(FileTransfer row)
    {
        row.ObservedBytes = null;
        row.ProgressObservedAt = null;
        lock (_sync) _active[row.FromSite + "|" + row.ToSite + "|" + row.Name] = new Entry { Row = row };
    }

    public void Observe(string targetSite, IEnumerable<(string Name, long Size)> files, DateTime listingStarted)
    {
        Entry[] entries;
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            if (_next.TryGetValue(targetSite, out var next) && next > now) return;
            _next[targetSite] = now.AddMilliseconds(200);
            entries = _active.Values.Where(e => e.Row.ToSite.Equals(targetSite, StringComparison.OrdinalIgnoreCase)).ToArray();
        }
        if (entries.Length == 0) return;
        var sizes = files.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().Size, StringComparer.OrdinalIgnoreCase);
        _store.UpdateJobTransient(_jobId, job =>
        {
            if (job.State != JobState.Running) return;
            foreach (var e in entries)
            {
                var row = e.Row;
                if (row.Status != "active" || listingStarted <= row.StartedAt ||
                    !sizes.TryGetValue(row.Name, out var bytes) || bytes <= 1) continue;
                // A new lower size is a real observation; never invent monotonic growth.
                var dt = (listingStarted - e.LastAt).TotalSeconds;
                row.Bps = e.LastBytes.HasValue && bytes >= e.LastBytes && dt > 0 ? (bytes - e.LastBytes.Value) / dt : 0;
                row.ObservedBytes = bytes;
                row.ProgressObservedAt = listingStarted;
                row.Seconds = Math.Max(.001, (listingStarted - row.StartedAt).TotalSeconds);
                e.LastBytes = bytes;
                e.LastAt = listingStarted;
            }
        });
        lock (_sync)
            foreach (var key in _active.Where(p => p.Value.Row.Status != "active").Select(p => p.Key).ToList()) _active.Remove(key);
    }
}
