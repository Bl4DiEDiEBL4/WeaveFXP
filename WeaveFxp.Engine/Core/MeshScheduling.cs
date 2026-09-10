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
    public DateTime RetryAt { get; }

    public MeshPendingDupe(DateTime now)
    {
        ReportedAt = now;
        RetryAt = now.AddSeconds(15);
    }

    public bool IsConfirmedByListing(DateTime listingStarted, long listedSize, long expectedSize) =>
        listingStarted > ReportedAt && expectedSize > 0 && listedSize >= expectedSize;
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
