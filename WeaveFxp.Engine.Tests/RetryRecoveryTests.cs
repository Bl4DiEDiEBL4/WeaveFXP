using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RetryRecoveryTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<AppSettings>("{}")!.WithDefaults();
        check(legacy.IncompleteRaceRetries == 3 && legacy.IncompleteRaceRetryDelaySeconds == 30, "old settings get three retries, thirty seconds apart");
        check(new AppSettings { IncompleteRaceRetries = 0 }.WithDefaults().IncompleteRaceRetries == 0, "zero disables automatic retries");
        var bounded = new AppSettings { IncompleteRaceRetries = 999, IncompleteRaceRetryDelaySeconds = 0 }.WithDefaults();
        check(bounded.IncompleteRaceRetries == 20 && bounded.IncompleteRaceRetryDelaySeconds == 1, "retry settings are bounded");
        var path = TempPath();
        var store = new JsonStore(path);
        var settings = store.Settings();
        settings.IncompleteRaceRetries = 2;
        settings.IncompleteRaceRetryDelaySeconds = 17;
        store.UpdateSettings(settings);
        var restored = new JsonStore(path).Settings();
        check(restored.IncompleteRaceRetries == 2 && restored.IncompleteRaceRetryDelaySeconds == 17, "retry settings survive save/reopen");

        await Task.WhenAll(new[] { false, true }.SelectMany(mesh => new[]
        {
            ChangedSource(check, mesh, grow: true),
            ChangedSource(check, mesh, grow: false),
            RetryUnchangedSource(check, mesh),
            BoundedRetries(check, mesh, 0),
            BoundedRetries(check, mesh, 2),
            CancelRetryWait(check, mesh),
            PauseRetryWait(check, mesh),
        }));
        await ChangedWhileFailing(check);
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), "weave-retry-tests-" + Guid.NewGuid().ToString("N"), "state.json");

    private static (WeaveEngine Engine, Job Job) Start(TransferServer source, TransferServer target, bool mesh, int retries, int delay = 1, bool longIdle = false)
    {
        var engine = new WeaveEngine(TempPath());
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = longIdle ? 10000 : 61;
        settings.IncompleteRaceRetries = retries;
        settings.IncompleteRaceRetryDelaySeconds = delay;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("destination", false, true));
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "source", ToSite = "destination", SourcePath = "/release", DestPath = "/release", ViaApi = true,
            MeshSites = mesh ? new() { "source", "destination" } : new()
        });
        return (engine, job);
    }

    private static bool Retrying(WeaveEngine e, string id) => e.Job(id)!.Events.Any(x => x.Message.StartsWith("incomplete race: retry "));
    private static int Passes(WeaveEngine e, string id) => e.Job(id)!.Events.Count(x => x.Message.StartsWith("race started (") || x.Message.StartsWith("mesh race started with"));

    private static async Task ChangedSource(Action<bool, string> check, bool mesh, bool grow)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        source.Reject = command => command == "PRET RETR payload.r00" ? "550 temporarily denied" : null;
        var (engine, job) = Start(source, target, mesh, retries: 0, longIdle: true);
        try
        {
            await Until(() => source.Rejections >= 7, engine, job.Id, 90);
            await Task.Delay(250);
            check(source.Rejections == 7, $"unchanged source stops after seven failures (mesh={mesh}, grow={grow})");
            var repaired = Enumerable.Repeat((byte)7, grow ? 64 : 16).ToArray();
            source.Reject = null;
            source.Files["payload.r00"] = repaired;
            source.Files["next.r01"] = new byte[48];
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\nnext.r01 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 15);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files.TryGetValue("payload.r00", out var actual) && actual.SequenceEqual(repaired),
                $"changed source recovers exhausted attempts without a whole-job retry (mesh={mesh}, grow={grow})");
            check(target.Files.ContainsKey("next.r01") && Passes(engine, job.Id) == 1, $"unrelated work proceeds in the same race (mesh={mesh}, grow={grow})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task RetryUnchangedSource(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var bytes = Enumerable.Repeat((byte)9, 32).ToArray();
        source.Files["payload.r00"] = bytes;
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        source.Reject = command => command == "PRET RETR payload.r00" ? "550 temporarily denied" : null;
        var sfvUploads = 0;
        target.BeforeCommand = command => { if (command == "STOR release.sfv") Interlocked.Increment(ref sfvUploads); return Task.CompletedTask; };
        var (engine, job) = Start(source, target, mesh, retries: 1, delay: 2);
        try
        {
            await Until(() => Retrying(engine, job.Id), engine, job.Id, 100);
            check(source.Rejections == 7 && !engine.Job(job.Id)!.Terminal, $"exhausted incomplete race waits for bounded retry (mesh={mesh})");
            source.Reject = null; // Same path, same size: only a fresh pass can recover.
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 20);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].SequenceEqual(bytes) && Passes(engine, job.Id) == 2,
                $"whole-job retry refreshes unchanged exhausted source (mesh={mesh})");
            check(sfvUploads == 1, $"retry does not reupload files already on destination (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task BoundedRetries(Action<bool, string> check, bool mesh, int retries)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("missing.r00 00000000\r\n");
        var (engine, job) = Start(source, target, mesh, retries);
        try
        {
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 45);
            check(engine.Job(job.Id)!.State == JobState.Failed && Passes(engine, job.Id) == retries + 1,
                $"missing SFV file fails after exactly {retries} extra attempts (mesh={mesh})");
            check(engine.Job(job.Id)!.Events.Count(x => x.Message.StartsWith("incomplete race: retry ")) == retries,
                $"retry count excludes initial attempt (mesh={mesh}, retries={retries})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task CancelRetryWait(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("missing.r00 00000000\r\n");
        var (engine, job) = Start(source, target, mesh, 3, delay: 60);
        try
        {
            await Until(() => Retrying(engine, job.Id), engine, job.Id, 20);
            var timer = Stopwatch.StartNew();
            engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 3);
            await Task.Delay(250);
            check(engine.Job(job.Id)!.State == JobState.Cancelled && Passes(engine, job.Id) == 1 && timer.Elapsed < TimeSpan.FromSeconds(3),
                $"cancel interrupts retry wait and never restarts job (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task PauseRetryWait(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("missing.r00 00000000\r\n");
        var (engine, job) = Start(source, target, mesh, 1);
        try
        {
            await Until(() => Retrying(engine, job.Id), engine, job.Id, 20);
            engine.PauseJob(job.Id);
            source.Files["missing.r00"] = new byte[32];
            await Task.Delay(1500);
            check(Passes(engine, job.Id) == 1 && !target.Files.ContainsKey("missing.r00"), $"paused retry cannot transfer (mesh={mesh})");
            engine.ResumeJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 15);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["missing.r00"].Length == 32, $"resume starts pending retry and completes (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task ChangedWhileFailing(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        source.Files["payload.r00"] = new byte[32];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        source.BeforeCommand = async command =>
        {
            if (command != "PRET RETR payload.r00" || Interlocked.Increment(ref calls) != 7) return;
            blocked.TrySetResult();
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        source.Reject = command => command == "PRET RETR payload.r00" && Volatile.Read(ref calls) <= 7 ? "550 temporary denial" : null;
        var (engine, job) = Start(source, target, false, 0, longIdle: true);
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(90));
            var repaired = Enumerable.Repeat((byte)19, 64).ToArray();
            source.Files["payload.r00"] = repaired;
            await Task.Delay(350); // Lister observes replacement before the older attempt fails.
            release.TrySetResult();
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 15);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].SequenceEqual(repaired),
                "source changed during final failed attempt is requeued with fresh size and attempts");
        }
        finally { release.TrySetResult(); await Stop(engine, job); }
    }

    private static async Task Stop(WeaveEngine engine, Job job)
    {
        if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
        await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id, 5);
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, string id, int seconds)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("Retry test timed out: " + string.Join("\n", engine.Job(id)!.Events.TakeLast(20).Select(x => x.Message)));
            await Task.Delay(25);
        }
    }
}
