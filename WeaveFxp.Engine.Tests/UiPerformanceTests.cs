using System.Diagnostics;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class UiPerformanceTests
{
    public static void Run(Action<bool, string> check)
    {
        var path = Path.Combine(Path.GetTempPath(), "weave-ui-tests-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new JsonStore(path);
        store.UpsertJob(new Job
        {
            Id = "race", Type = JobType.Race, State = JobState.Running, RaceCoverageKnown = true, FilesCovered = 1,
            FilesTotal = 2, FilesDone = 2, CumulativeBytes = 100, CreatedAt = DateTime.UtcNow,
            Request = new() { Race = true, MeshSites = new() { "a", "b" } },
            Files = Enumerable.Range(0, 1200).Select(i => new FileTransfer { Name = "payload.r00", Status = "dupe" })
                .Concat(new[] { new FileTransfer { Name = "payload.r01", Status = "done", Size = 100 } }).ToList(),
            Events = Enumerable.Range(0, 1000).Select(i => new JobEvent { Message = "event " + i }).ToList()
        });
        check(!new MeshPendingDupe(DateTime.UtcNow).IsConfirmedByListing(DateTime.UtcNow.AddSeconds(1), 200, 100), "larger destination keeps occupancy suppressed");
        var summary = store.JobsForUi().Single();
        check(summary.Files.Count == 0 && summary.Events.Count == 0, "collapsed UI rows do not copy attempt/event history");
        check(summary.RaceCoverageKnown && summary.FilesCovered == 1 && summary.Percent == 50,
            "old successful/dupe attempts cannot inflate current mesh coverage");
        check(store.JobHeader("race")!.RaceCoverageKnown && store.JobHeader("race")!.FilesCovered == 1,
            "job header preserves current coverage");
        summary.Request.MeshSites.Clear();
        check(store.Job("race")!.Request.MeshSites.Count == 2, "UI headers cannot mutate the stored request");
        var details = store.JobsForUi(new[] { "race" }).Single();
        check(details.Files.Count == 500 && details.Events.Count == 200 && details.Files.Last().Status == "done",
            "expanded UI shows bounded latest details");
        check(store.Job("race")!.Files.Count == 1201 && store.Job("race")!.Events.Count == 1000,
            "UI rendering limits preserve full persisted/API history");
        store.UpdateJob("race", j => j.State = JobState.Succeeded);
        check(store.JobsForUi().Single().SpeedBps == 0, "terminal job summary never shows stale live speed");
        var cached = store.JobsForUi().Single();
        store.UpdateJobTransient("race", j => j.Files.Add(new FileTransfer { Name = "new.r02", Status = "done", Size = 900 }));
        var changed = store.JobsForUi().Single();
        check(changed.CumulativeBytes == 1000 && changed.FilesDone == 2 && changed.FilesTotal == 2 && changed.FilesCovered == 1,
            "terminal history changes refresh own contribution without inflating verified coverage");
        store.UpdateJobTransient("race", j => j.RaceCoverageKnown = false);
        check(store.JobsForUi().Single().FilesTotal == 3,
            "legacy jobs without verified coverage retain the historical-name fallback");
        check(store.HotJobCount == 1 && store.Job("race")!.Files.Count == 1202,
            "health job count does not clone or alter full history");
        store.UpsertJob(new Job { Id = "manual", Type = JobType.Download, Events = new() { new() { Message = "download event" } } });
        check(store.JobsForUi(includeManualEvents: true).Single(j => j.Id == "manual").Events.Single().Message == "download event",
            "browser manual queue retains its event display");
    }

    public static async Task LargerDupe(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        source.Files["release.sfv"] = System.Text.Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        // The destination has a larger in-progress version than the source listing.
        target.Files["payload.r00"] = new byte[64];
        target.Reject = command => command == "PRET STOR payload.r00" ? "553 payload.r00: File exists." : null;
        var path = Path.Combine(Path.GetTempPath(), "weave-dupe-tests-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.IncompleteRaceRetries = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("destination", false, true));
        var job = engine.StartFxp(new TransferRequest { Race = true, FromSite = "source", ToSite = "destination", SourcePath = "/release", DestPath = "/release", MeshSites = new() { "source", "destination" }, ViaApi = true });
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (target.Rejections == 0 && DateTime.UtcNow < deadline) await Task.Delay(25);
            check(target.Rejections > 0, "larger-target regression injects a real FTP dupe");
            await Task.Delay(1200);
            check(target.Rejections == 1 && engine.Job(job.Id)!.Files.Count(f => f.Name == "payload.r00") <= 2,
                "larger destination cannot cause a 25ms dupe retry/history storm");
            check(engine.Job(job.Id)!.State != JobState.Succeeded, "larger target does not falsely prove source completion");
            source.Files["payload.r00"] = new byte[64];
            deadline = DateTime.UtcNow.AddSeconds(10);
            while (!engine.Job(job.Id)!.Terminal && DateTime.UtcNow < deadline) await Task.Delay(25);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Rejections == 1,
                "matching fresh source/target sizes finish without another STOR");
        }
        finally { if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id); }
    }

    public static async Task RealData(string path)
    {
        var timer = Stopwatch.StartNew();
        var engine = new WeaveEngine(path);
        var startup = timer.Elapsed.TotalMilliseconds;
        timer.Restart();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var old = engine.Jobs();
        var full = timer.Elapsed.TotalMilliseconds;
        var fullBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        timer.Restart(); allocated = GC.GetAllocatedBytesForCurrentThread();
        var summaries = engine.JobsForUi();
        var compact = timer.Elapsed.TotalMilliseconds;
        var compactBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        var id = old.OrderByDescending(j => j.Files.Count).First().Id;
        engine.Changed += () => engine.JobsForUi();
        timer.Restart();
        var accepted = engine.RetryJob(id);
        var retry = timer.Elapsed.TotalMilliseconds;
        engine.CancelJob(id);
        await Task.Delay(100);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { jobs = old.Count, fileRows = old.Sum(j => j.Files.Count), startupMs = startup, oldUiFullSnapshotMs = full, newUiSummaryMs = compact, fullAllocatedMB = fullBytes / 1e6, summaryAllocatedMB = compactBytes / 1e6, retryAccepted = accepted, retryActionMs = retry, testCopyOnly = true }));
    }
}
