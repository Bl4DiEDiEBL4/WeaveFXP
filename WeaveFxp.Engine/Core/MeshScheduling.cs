namespace WeaveFxp.Engine.Core;

// Owned by the mesh lock. Only changed files rebuild their possible routes.
internal sealed class MeshScoreboard<T>
{
    private readonly Dictionary<string, List<(T Pick, long Score)>> _files = new(StringComparer.OrdinalIgnoreCase);
    private List<(T Pick, long Score)>? _ordered;

    public void Replace(string file, List<(T Pick, long Score)> candidates)
    {
        if (candidates.Count == 0) _files.Remove(file);
        else _files[file] = candidates;
        _ordered = null;
    }

    public IReadOnlyList<(T Pick, long Score)> Ordered =>
        _ordered ??= _files.Values.SelectMany(x => x).OrderByDescending(x => x.Score).ToList();
}

internal sealed class MeshPendingDupe
{
    public DateTime ReportedAt { get; }
    private DateTime _lastProgressAt;
    private long? _lastListedSize;

    public MeshPendingDupe(DateTime now)
    {
        ReportedAt = now;
        _lastProgressAt = now;
    }

    // A dupe proves occupancy, not completeness. Time alone cannot make STOR safe:
    // require a fresh disappearance of a file we actually saw after the refusal.
    public bool ObserveListing(DateTime listingStarted, long? listedSize)
    {
        if (listingStarted <= ReportedAt) return false;
        if (listedSize is null) return _lastListedSize.HasValue;
        if (_lastListedSize != listedSize)
        {
            _lastListedSize = listedSize;
            _lastProgressAt = listingStarted;
        }
        return false;
    }

    // Stalled occupancy can end incomplete through the normal idle/retry policy;
    // it must neither keep the race alive forever nor generate another attempt.
    public bool IsWaitingForProgress(DateTime now) => now < _lastProgressAt.AddSeconds(15);

    public bool IsConfirmedByListing(DateTime listingStarted, long listedSize, long expectedSize) =>
        // A larger destination is not evidence that the smaller source is ready.
        // Clearing occupancy here made every poll try STOR and receive another dupe.
        listingStarted > ReportedAt && expectedSize > 0 && listedSize == expectedSize;
}

internal static class MeshScheduling
{
    public static int WorkerCount(IEnumerable<(int Logins, int Downloads, int Uploads)> sites)
    {
        var capacities = sites.ToList();
        var streams = Math.Min(capacities.Sum(x => x.Downloads), capacities.Sum(x => x.Uploads));
        streams = Math.Min(streams, capacities.Sum(x => Math.Min(x.Logins, x.Downloads + x.Uploads)) / 2);
        return Math.Clamp(streams, 1, 256);
    }
}
