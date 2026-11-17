using System.Diagnostics;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RaceFinalizationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var gate = new object();
        void Verify(bool ok, string name) { lock (gate) check(ok, name); }
        var statePath = Path.Combine(Path.GetTempPath(), "weave-coverage-ui-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new JsonStore(statePath);
        store.UpsertJob(new Job { Id = "covered", Type = JobType.Race, State = JobState.Succeeded,
            RaceCoverageKnown = true, FilesTotal = 1, FilesCovered = 1,
            Files = new() { new() { Name = "current.rar", Status = "done" }, new() { Name = "removed.nfo", Status = "dupe" } } });
        var header = store.JobsForUi().Single();
        check(header.FilesTotal == 1 && header.FilesCovered == 1 && header.FilesDone == 1,
            "UI preserves verified current coverage instead of resurrecting historical attempt names");
        typeof(JsonStore).GetMethod("FlushLocked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(store, new object[] { Enum.ToObject(typeof(JsonStore).GetNestedType("StateSections", System.Reflection.BindingFlags.NonPublic)!, 63) });
        check(new JsonStore(statePath).JobsForUi().Single().FilesTotal == 1,
            "verified denominator survives save and reload with old attempts retained");
        var quiet = new RaceQuietCompletion(TimeSpan.FromMinutes(1));
        var now = DateTime.UtcNow;
        check(!quiet.Observe(true, 1, now) && !quiet.Observe(true, 2, now.AddSeconds(4)) &&
            !quiet.Observe(true, 2, now.AddSeconds(8)) && quiet.Observe(true, 2, now.AddSeconds(9)),
            "new file evidence restarts the five-second quiet completion window");
        check(!quiet.Observe(false, 2, now.AddSeconds(10)) && !quiet.Observe(true, 2, now.AddSeconds(16)),
            "a stale or failed listing discards the earlier quiet completion window");
        await QuietCoverage(Verify, true);
        await QuietCoverage(Verify, false);
        await Task.WhenAll(new[] { false, true }.SelectMany(mesh => new[]
        {
            Occupied(Verify, mesh, repair: false),
            Occupied(Verify, mesh, repair: true),
            ExpectedUnion(Verify, mesh),
            ListingFailure(Verify, mesh),
            FailedFinalSnapshot(Verify, mesh),
            OccupiedRecovery(Verify, mesh),
            GrowingOpponent(Verify, mesh),
        }));
    }

    private static (WeaveEngine Engine, Job Job) Start(TransferServer source, TransferServer target, bool mesh, int retries = 0, int idle = 1000)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-finalization-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = idle;
        settings.IncompleteRaceRetries = retries;
        settings.IncompleteRaceRetryDelaySeconds = 1;
        settings.RaceCompletionRecheckSeconds = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, ViaApi = true, FromSite = "source", ToSite = "target", SourcePath = "/release", DestPath = "/release",
            MeshSites = mesh ? new() { "source", "target" } : new()
        });
        return (engine, job);
    }

    private static async Task QuietCoverage(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[64];
        source.Files["vanishing.nfo"] = new byte[12];
        var (engine, job) = Start(source, target, mesh);
        try
        {
            await Until(() => engine.Job(job.Id)!.FilesCovered == 2 && engine.Job(job.Id)!.Files.Count(f => f.Status == "done") >= 2, engine, job, 8);
            await Task.Delay(250);
            source.Files.TryRemove("vanishing.nfo", out _);
            target.Files.TryRemove("vanishing.nfo", out _);
            var timer = Stopwatch.StartNew();
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 9);
            var done = engine.Job(job.Id)!;
            check(done.State == JobState.Succeeded && done.FilesCovered == 1 && done.FilesTotal == 1 && done.BytesTotal == 64,
                $"quiet no-SFV completion uses current files and sizes, not historical peaks (mesh={mesh})");
            check(timer.Elapsed < TimeSpan.FromSeconds(9), $"verified quiet completion avoids a full idle timeout (mesh={mesh})");
            check(done.FilesDone >= 2 && done.BytesDone >= 76, $"own transfer history survives a shrinking current denominator (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task Occupied(Action<bool, string> check, bool mesh, bool repair)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["sample.mkv"] = new byte[64];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("sample.mkv 00000000\r\n");
        target.Files["sample.mkv"] = new byte[16];
        target.Reject = cmd => cmd == "PRET STOR sample.mkv" ? "553 file is being uploaded by another user" : null;
        var (engine, job) = Start(source, target, mesh, retries: 2, idle: 61);
        try
        {
            await Until(() => target.Rejections > 0, engine, job, 5);
            if (repair)
            {
                await Task.Delay(700);
                check(!engine.Job(job.Id)!.Terminal, $"partial occupied file is never premature success (mesh={mesh})");
                target.Files["sample.mkv"] = new byte[64];
            }
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, repair ? 6 : 23);
            var done = engine.Job(job.Id)!;
            check(target.Rejections == 1, $"unchanged occupied destination gets one STOR attempt across retries (mesh={mesh}, repair={repair})");
            check(done.State == (repair ? JobState.Succeeded : JobState.Failed),
                $"occupied race ends honestly from fresh destination evidence (mesh={mesh}, repair={repair})");
            check(done.Events.Count(e => e.Message.StartsWith("incomplete race: retry ")) == 0,
                $"unchanged occupancy does not restart whole race (mesh={mesh}, repair={repair})");
            check(!done.Files.Any(f => f.Name == "sample.mkv" && f.Status == "done"),
                $"external completion is coverage, not an own upload (mesh={mesh}, repair={repair})");
            if (!repair)
                check(done.Error.Contains("occupied", StringComparison.OrdinalIgnoreCase) && done.FilesCovered < done.FilesTotal,
                    $"stalled occupancy reports a concrete incomplete reason (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task ExpectedUnion(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("external.r00 00000000\r\nmissing.r01 00000000\r\n");
        target.Files["external.r00"] = new byte[32];
        var (engine, job) = Start(source, target, mesh, idle: 61);
        try
        {
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 10);
            var done = engine.Job(job.Id)!;
            check(done.State == JobState.Failed && done.FilesTotal == 3 && done.FilesCovered == 2,
                $"current listing and SFV form a union including absent expected files (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task ListingFailure(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        var (engine, job) = Start(source, target, mesh);
        try
        {
            await Until(() => engine.Job(job.Id)!.FilesCovered == 1, engine, job, 5);
            target.Reject = cmd => cmd.StartsWith("STAT ") ? "550 listing unavailable" : null;
            await Until(() => target.Rejections > 0, engine, job, 4);
            await Task.Delay(5500);
            check(!engine.Job(job.Id)!.Terminal, $"failed listing cannot finish from old matching snapshots (mesh={mesh})");
            target.Reject = null;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 9);
            check(engine.Job(job.Id)!.State == JobState.Succeeded, $"fresh listings restore bounded quiet completion (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task OccupiedRecovery(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[64];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        target.Files["payload.r00"] = new byte[16];
        var storAttempts = 0;
        target.BeforeCommand = cmd => { if (cmd == "PRET STOR payload.r00") Interlocked.Increment(ref storAttempts); return Task.CompletedTask; };
        target.Reject = cmd => cmd == "PRET STOR payload.r00" ? "553 File exists." : null;
        var (engine, job) = Start(source, target, mesh);
        try
        {
            await Until(() => Volatile.Read(ref storAttempts) == 1, engine, job, 5);
            await Task.Delay(750);
            target.Reject = cmd => cmd.StartsWith("STAT ") ? "550 listing unavailable" : null;
            target.Files.TryRemove("payload.r00", out _);
            await Task.Delay(750);
            check(Volatile.Read(ref storAttempts) == 1 && !engine.Job(job.Id)!.Terminal,
                $"failed listing is not disappearance evidence for an occupied file (mesh={mesh})");
            target.Reject = null;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 9);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && Volatile.Read(ref storAttempts) == 2 &&
                target.Files["payload.r00"].Length == 64,
                $"fresh disappearance reopens exactly one repair attempt (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    public static async Task SnapshotFailures(Action<bool, string> check)
    {
        foreach (var mesh in new[] { false, true }) await FailedFinalSnapshot(check, mesh);
    }

    private static async Task FailedFinalSnapshot(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        var (engine, job) = Start(source, target, mesh, idle: 61);
        try
        {
            await Until(() => engine.Job(job.Id)!.FilesCovered == 1, engine, job, 5);
            target.Reject = cmd => cmd.StartsWith("STAT ") ? "550 listing unavailable" : null;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 10);
            check(engine.Job(job.Id)!.State == JobState.Failed && target.Rejections > 0,
                $"idle termination cannot promote cached full coverage after verification failed (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task GrowingOpponent(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[64];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        target.Files["payload.r00"] = new byte[16];
        target.Reject = cmd => cmd == "PRET STOR payload.r00" ? "553 upload already in progress" : null;
        var (engine, job) = Start(source, target, mesh, retries: 2, idle: 61);
        try
        {
            await Until(() => target.Rejections > 0, engine, job, 5);
            for (var i = 0; i < 7; i++)
            {
                await Task.Delay(1000);
                target.Files["payload.r00"] = new byte[24 + i];
            }
            check(!engine.Job(job.Id)!.Terminal && target.Rejections == 1,
                $"opponent growth remains progress beyond the short idle timeout (mesh={mesh})");
            target.Files["payload.r00"] = new byte[64];
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 6);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Rejections == 1,
                $"growing opponent can complete without a redundant STOR or whole-job retry (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task Stop(WeaveEngine engine, Job job)
    {
        if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
        await Until(() => engine.Job(job.Id)!.Terminal, engine, job, 5);
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, Job job, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new Exception("Finalization timeout: " + string.Join("\n", engine.Job(job.Id)!.Events.TakeLast(15).Select(e => e.Message)));
            await Task.Delay(25);
        }
    }
}
