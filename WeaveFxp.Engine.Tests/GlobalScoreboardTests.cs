using WeaveFxp.Engine.Core;

internal static class GlobalScoreboardTests
{
    public static void Run(Action<bool, string> check)
    {
        var board = new GlobalMeshScoreboard<string>();
        var shared = new object();
        var destination = new object();
        var wakes = 0;
        var reservations = 0;
        GlobalMeshScoreboard<string>.Candidate Candidate(string name, long score, object? source = null,
            string? key = null, bool available = true) => new(name, score, key ?? name,
                source ?? shared, source is null ? destination : new object(), () => available,
                () => { reservations++; return new Cleanup(() => reservations--); }, () => 1, () => 1);

        using var old = board.Register("old", 1, () => { });
        using var fresh = board.Register("fresh", 1, () => wakes++);
        board.Publish(old, new[] { Candidate("bulk", 2_000_000_000) });
        board.Publish(fresh, new[] { Candidate("sfv", 5_000_000_000) });
        check(board.TryTake(old) is null && wakes > 0, "global scoreboard gives new SFV precedence over another race's bulk");
        using (var sfv = board.TryTake(fresh))
        {
            check(sfv?.Candidate.Value == "sfv", "higher-scoring race receives its claim");
            board.Publish(fresh, new[] { Candidate("next-sfv", 5_000_000_000) });
            using var bulk = board.TryTake(old);
            check(bulk?.Candidate.Value == "bulk", "race with all workers active does not block another race");
        }
        check(reservations == 0, "finishing claims releases every reservation");

        board.Publish(old, new[] { Candidate("independent", 1, new object()) });
        using (var independent = board.TryTake(old))
            check(independent?.Candidate.Value == "independent", "higher-priority race does not block independent site pair");
        board.Publish(fresh, new[] { Candidate("blocked-sfv", 100, available: false) });
        board.Publish(old, new[] { Candidate("ready-bulk", 10) });
        using (var ready = board.TryTake(old))
            check(ready?.Candidate.Value == "ready-bulk", "unavailable high-score route does not block runnable transfer");

        board.Publish(fresh, new[] { Candidate("paused-sfv", 100) });
        board.Publish(old, new[] { Candidate("pause-bulk", 10) });
        board.SetPaused("fresh", true);
        using (var ready = board.TryTake(old))
            check(ready is not null, "paused race withdraws from global ranking");
        board.SetPaused("fresh", false);
        using (var resumed = board.TryTake(fresh))
            check(resumed?.Candidate.Value == "paused-sfv", "resumed race reenters ranking");

        board.Publish(old, new[] { Candidate("first-copy", 10, key: "site|/release/file") });
        board.Publish(fresh, new[] { Candidate("second-copy", 100, key: "SITE|/release/file") });
        using (var first = board.TryTake(fresh))
        {
            check(first is not null && board.TryTake(old) is null, "same destination file cannot be claimed by two races");
            fresh.Dispose();
            check(reservations == 1, "unregister leaves active transfer reserved until its worker finishes");
        }
        using (var retry = board.TryTake(old))
            check(retry is not null, "released destination becomes available after cancellation cleanup");
        board.Publish(fresh, new[] { Candidate("stale", long.MaxValue) });
        check(board.TryTake(fresh) is null, "late publication cannot revive an unregistered race");

        using var replacement = board.Register("fresh", 1, () => { });
        board.Publish(replacement, new[] { Candidate("replacement", 100) });
        fresh.Dispose();
        using (var current = board.TryTake(replacement))
            check(current is not null, "old run cleanup cannot unregister a retry with the same job ID");

        var fair = new GlobalMeshScoreboard<string>();
        using var a = fair.Register("a", 1, () => { });
        using var b = fair.Register("b", 1, () => { });
        fair.Publish(a, new[] { Candidate("a1", 10) });
        fair.Publish(b, new[] { Candidate("b1", 10) });
        using (var first = fair.TryTake(a)) check(first is not null, "equal-score first selection is deterministic");
        fair.Publish(a, new[] { Candidate("a2", 10) });
        check(fair.TryTake(a) is null, "equal-score races rotate rather than letting the fastest worker monopolize slots");
        using (var second = fair.TryTake(b)) check(second is not null, "waiting equal-score race gets next slot");

        var concurrent = new GlobalMeshScoreboard<string>();
        using var owner = concurrent.Register("parallel", 8, () => { });
        concurrent.Publish(owner, Enumerable.Range(0, 8).Select(i => Candidate("route-" + i, i, key: "same-file")));
        var claims = new System.Collections.Concurrent.ConcurrentBag<GlobalMeshScoreboard<string>.Claim>();
        Parallel.For(0, 8, _ => { if (concurrent.TryTake(owner) is { } claim) claims.Add(claim); });
        check(claims.Count == 1, "parallel workers cannot claim duplicate destination reservations");
        foreach (var claim in claims) { claim.Dispose(); claim.Dispose(); }
        check(reservations == 0, "claim disposal is idempotent");
    }

    private sealed class Cleanup(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
