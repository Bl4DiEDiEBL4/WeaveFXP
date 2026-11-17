using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

internal static class RaceRetryLoadTests
{
    private static string TempPath() => Path.Combine(Path.GetTempPath(), "weave-retry-load-" + Guid.NewGuid().ToString("N"), "state.json");
    private static TransferRequest Request(bool dry = false) => new()
    {
        Race = true, FromSite = "source", ToSite = "target", SourcePath = "/release", DestPath = "/release", DryRun = dry
    };

    public static async Task Run(Action<bool, string> check)
    {
        await QueueRecovery(check);
        await CapacityChanges(check);
        await ListingTimeout(check);
        await RetryWaitReleasesCapacity(check);
        SnapshotCost(check);
        foreach (var mesh in new[] { false, true })
        {
            await StorageFailure(check, mesh, finalReply: false);
            await StorageFailure(check, mesh, finalReply: true);
        }
        check(!FxpTransfer.IsInsufficientStorageError(new IOException("550 file 552.rar not found")), "numeric file names do not trip the storage guard");
        check(!FxpTransfer.IsInsufficientStorageError(new IOException("426 transfer aborted")), "ordinary transfer errors retain normal retry handling");
        check(FxpTransfer.IsInsufficientStorageError(new IOException("peer failed", new IOException("552 Exceeded storage allocation"))), "wrapped storage replies retain their cause");
    }

    private static async Task Until(Func<bool> done)
    {
        var timeout = Stopwatch.StartNew();
        while (!done())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(12)) throw new TimeoutException("Retry load fixture did not progress");
            await Task.Delay(20);
        }
    }

    private static async Task QueueRecovery(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeCommand = command => command.StartsWith("STAT") ? release.Task : Task.CompletedTask;
        var path = TempPath();
        var store = new JsonStore(path);
        var settings = store.Settings();
        settings.MaxConcurrentRaceJobs = 2;
        settings.RaceMaxIdleCycles = 10000;
        settings.RacePollIntervalMs = 100;
        settings.IncompleteRaceRetries = 0;
        store.UpdateSettings(settings);
        var seeded = new Dictionary<string, Job>();
        for (var i = 0; i < 30; i++) seeded["recovery-" + i] = new Job
        {
            Id = "recovery-" + i, State = JobState.Failed, Type = JobType.Race,
            CreatedAt = DateTime.UtcNow, Request = Request(),
            Files = new() { new() { Name = "old.rar", Status = "done", Size = 50_000_000 } }
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(path)!, "jobs.json"),
            JsonSerializer.Serialize(seeded, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var engine = new WeaveEngine(path);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var all = Enumerable.Range(0, 30).Select(i => "recovery-" + i).ToList();
        try
        {
            var timer = Stopwatch.StartNew();
            var accepted = await Task.Run(() => all.Count(engine.RetryJob));
            var acceptedMs = timer.Elapsed.TotalMilliseconds;
            await Until(() => engine.JobsForUi().Count(j => j.State == JobState.Running) >= 2);
            var jobs = engine.JobsForUi();
            check(accepted == 30 && jobs.Count(j => j.State == JobState.Running) == 2 && jobs.Count(j => j.State == JobState.Queued) == 28,
                "thirty real race retries start only the configured two runners");
            check(jobs.Where(j => j.State == JobState.Queued).All(j => j.StartedAt == default && j.Events.All(e => e.Message != "job started")),
                "queued retries have no worker start time or watchdog activity");
            check(!engine.RetryJob("recovery-2"), "double-click cannot register a second queued run");
            check(jobs.Where(j => !j.Terminal).All(j => j.FilesDone == 0 && j.CumulativeBytes == 0),
                "a retry's old successful history does not inflate current-run counters");

            engine.PauseJob("recovery-2");
            engine.CancelJob("recovery-3");
            var fresh = engine.StartFxp(Request(dry: true));
            all.Add(fresh.Id);
            var spread = engine.StartSpread(new SpreadRequest
            {
                Race = true, DryRun = true, FromSite = "source", ToSites = new() { "target" }, SourcePath = "/release"
            });
            all.AddRange(spread.Jobs.Select(j => j.Id));
            await Task.Delay(150);
            check(engine.JobsForUi().Count(j => j.State == JobState.Running) == 2 && engine.Job(fresh.Id)!.State == JobState.Queued &&
                spread.Jobs.All(j => engine.Job(j.Id)!.State == JobState.Queued), "announces, spreads and retries share the same global race limit");

            engine.CancelJob("recovery-0");
            await Until(() => engine.Job(fresh.Id)!.Terminal && spread.Jobs.All(j => engine.Job(j.Id)!.Terminal) && engine.Job("recovery-4")!.State == JobState.Running);
            check(engine.Job(fresh.Id)!.State == JobState.Succeeded && spread.Jobs.All(j => engine.Job(j.Id)!.State == JobState.Succeeded),
                "fresh announces and spreads overtake the queued retry backlog");
            check(engine.Job("recovery-2") is { State: JobState.Queued, Paused: true } && engine.Job("recovery-3")!.State == JobState.Cancelled,
                "paused and cancelled pending retries never consume a running slot");
            engine.ResumeJob("recovery-2");
            engine.CancelJob("recovery-1");
            await Until(() => engine.Job("recovery-2")!.State == JobState.Running);
            check(engine.Job("recovery-2")!.State == JobState.Running, "resuming a queued retry starts it when a permit is available");
            Console.WriteLine($"LOAD: accepted {accepted} retries in {acceptedMs:0.0}ms; active limit=2");
        }
        finally
        {
            foreach (var id in all) engine.CancelJob(id);
            release.TrySetResult();
            await Task.Delay(200);
        }
    }

    private static void SnapshotCost(Action<bool, string> check)
    {
        var store = new JsonStore(TempPath());
        var history = Enumerable.Range(0, 4000).Select(i => new FileTransfer { Name = "old-" + i + ".rar", Status = "done", Size = 50_000_000 }).ToList();
        for (var i = 0; i < 30; i++) store.UpsertJob(new Job
        {
            Id = "snapshot-" + i, Type = JobType.Race, State = JobState.Running, RaceCoverageKnown = true,
            Files = history, FilesDone = 1, CumulativeBytes = 99, FilesTotal = 10, FilesCovered = 2
        });
        store.JobsForUi();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        for (var frame = 0; frame < 25; frame++)
        {
            store.UpdateJobTransient("snapshot-0", j => j.SpeedBps++);
            var rows = store.JobsForUi();
            if (rows.Any(j => j.FilesDone != 1 || j.CumulativeBytes != 99)) throw new Exception("Old retry history leaked into live counts");
        }
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        check(bytes < 12_000_000, "25 UI frames with 120,000 historical attempts allocate less than 12MB");
        check(timer.Elapsed < TimeSpan.FromSeconds(2), "large retry histories do not stall summary reads");
        var detail = store.JobsForUi(new[] { "snapshot-0" }).Single(j => j.Id == "snapshot-0");
        check(detail.Files.Count == 500 && store.Job("snapshot-0")!.Files.Count == 4000, "the summary optimization preserves full attempts and bounded expanded details");
        Console.WriteLine($"LOAD: 25 UI snapshots, 30 jobs x 4000 attempts: {timer.Elapsed.TotalMilliseconds:0.0}ms, {bytes / 1e6:0.00}MB allocated");
    }

    private static async Task CapacityChanges(Action<bool, string> check)
    {
        var queue = new ManualTransferQueue();
        var limit = new ManualTransferQueue.Limit("race", 1);
        using var first = await queue.Enqueue("one", CancellationToken.None, limit);
        var next = queue.Enqueue("two", CancellationToken.None, limit);
        var third = queue.Enqueue("three", CancellationToken.None, limit);
        queue.SetCapacity("race", 2);
        using var second = await next.WaitAsync(TimeSpan.FromSeconds(2));
        check(!third.IsCompleted, "increasing race capacity dispatches only the additional configured slot");
        queue.SetCapacity("race", 1);
        second.Dispose();
        check(!third.IsCompleted, "lowering capacity keeps existing work alive without admitting more jobs");
        first.Dispose();
        using var last = await third.WaitAsync(TimeSpan.FromSeconds(2));
        check(third.IsCompletedSuccessfully, "pending jobs use changed limits without a restart");
    }

    private static async Task RetryWaitReleasesCapacity(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("missing.r00 00000000\r\n");
        var engine = new WeaveEngine(TempPath());
        var settings = engine.Settings(false);
        settings.MaxConcurrentRaceJobs = 1;
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 61;
        settings.IncompleteRaceRetries = 1;
        settings.IncompleteRaceRetryDelaySeconds = 10;
        settings.RaceCompletionRecheckSeconds = 0;
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var recovery = engine.StartFxp(Request());
        try
        {
            await Until(() => engine.Job(recovery.Id)!.Events.Any(e => e.Message.StartsWith("incomplete race: retry")));
            var timer = Stopwatch.StartNew();
            var fresh = engine.StartFxp(Request(dry: true));
            await Until(() => engine.Job(fresh.Id)!.Terminal);
            check(timer.Elapsed < TimeSpan.FromSeconds(2) && engine.Job(fresh.Id)!.State == JobState.Succeeded &&
                engine.Job(recovery.Id) is { State: JobState.Queued, Terminal: false },
                "a sleeping retry releases its worker permit so fresh races do not wait for its delay");
        }
        finally { engine.CancelJob(recovery.Id); }
    }

    private static async Task ListingTimeout(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeCommand = command => command.StartsWith("STAT") ? release.Task : Task.CompletedTask;
        var site = server.Site("stalled", true, false);
        site.TimeoutSeconds = 1;
        try
        {
            using var client = await FtpClient.DialAndLoginAsync(FtpClient.Config.FromSite(site));
            var timer = Stopwatch.StartNew();
            Exception? failure = null;
            try { await client.ListAsync("/release").WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception ex) { failure = ex; }
            check(failure is TimeoutException && failure.Message.StartsWith("FTP listing timed out") && timer.Elapsed < TimeSpan.FromSeconds(3),
                "a silent STAT reply obeys the configured timeout and drops its connection");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task StorageFailure(Action<bool, string> check, bool mesh, bool finalReply)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        var rejected = 0;
        if (finalReply)
            source.TransferFinalReply = command => command == "RETR payload.r00"
                ? "426 Download failed: source upload failed: cannot create file: disk full: free space 0.0 MB is below free_space_mb 2048 MB" : null;
        else target.Reject = command =>
        {
            if (command != "PRET STOR payload.r00") return null;
            Interlocked.Increment(ref rejected);
            return "552 Requested file action aborted: exceeded storage allocation";
        };
        var engine = new WeaveEngine(TempPath());
        var settings = engine.Settings(false);
        settings.IncompleteRaceRetries = 3;
        settings.RaceCompletionRecheckSeconds = 300;
        settings.RacePollIntervalMs = 25;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var request = Request();
        if (mesh) request.MeshSites = new() { "source", "target" };
        var job = engine.StartFxp(request);
        try
        {
            await Until(() => engine.Job(job.Id)!.Terminal);
            var failed = engine.Job(job.Id)!;
            check(failed.State == JobState.Failed && failed.Error.Contains("insufficient server storage"), $"storage rejection ends with its actual cause (mesh={mesh}, final={finalReply})");
            check(failed.Events.All(e => !e.Message.StartsWith("incomplete race: retry")) && (finalReply || rejected == 1),
                $"storage rejection does not trigger per-file or whole-race retry storms (mesh={mesh}, final={finalReply})");
            target.Reject = null;
            source.TransferFinalReply = null;
            // The final-reply fixture wrote bytes before the injected error, so remove
            // only that fixture's file to also exercise a genuine upload on recovery.
            target.Files.TryRemove("payload.r00", out _);
            check(engine.RetryJob(job.Id), $"manual retry is accepted after storage is repaired (mesh={mesh}, final={finalReply})");
            await Until(() => engine.Job(job.Id)!.Terminal);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].Length == 4096,
                $"recovery after storage repair transfers and verifies the release (mesh={mesh}, final={finalReply})");
        }
        finally { engine.CancelJob(job.Id); }
    }
}
