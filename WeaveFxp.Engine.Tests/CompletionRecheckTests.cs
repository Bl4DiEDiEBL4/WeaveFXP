using System.Collections.Concurrent;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class CompletionRecheckTests
{
    public static async Task Run(Action<bool, string> check)
    {
        check(new AppSettings().WithDefaults().RaceCompletionRecheckSeconds == 300 &&
            new AppSettings { RaceCompletionRecheckSeconds = -1 }.WithDefaults().RaceCompletionRecheckSeconds == 0 &&
            new AppSettings { RaceCompletionRecheckSeconds = 9000 }.WithDefaults().RaceCompletionRecheckSeconds == 3600,
            "completion recheck has a bounded five-minute default; zero disables extra waiting");
        var settingsPath = Path.Combine(Path.GetTempPath(), "weave-recheck-settings-" + Guid.NewGuid().ToString("N"), "state.json");
        var settingsStore = new JsonStore(settingsPath);
        var config = settingsStore.Settings();
        config.RaceCompletionRecheckSeconds = 17;
        settingsStore.UpdateSettings(config);
        check(new JsonStore(settingsPath).Settings().RaceCompletionRecheckSeconds == 17, "completion recheck window survives settings reload");
        foreach (var mesh in new[] { false, true })
        {
            await Recheck(check, mesh, "complete");
            foreach (var scenario in new[] { "missing", "partial", "hint", "listing", "sfv", "truncated", "mismatch", "sample", "removed", "cancelled", "changing", "no-marker", "blocked", "policy-change", "nested-marker", "config-change" })
                await Recheck(check, mesh, scenario);
            foreach (var outcome in new[] { "complete", "expire", "cancel" }) await LateOpponent(check, mesh, outcome);
        }
    }

    private static async Task Recheck(Action<bool, string> check, bool mesh, string scenario)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        foreach (var server in new[] { source, target })
        {
            server.Files["payload.r00"] = new byte[64];
            server.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        }
        target.Listing = path => (path.TrimEnd('/').EndsWith("/Sample") ? Enumerable.Empty<RemoteEntry>() :
                target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length }))
            .Concat(scenario == "nested-marker" && !path.TrimEnd('/').EndsWith("/Sample")
                ? new[] { new RemoteEntry { Name = "Sample", Type = "dir" } }
                : scenario == "no-marker" ? Array.Empty<RemoteEntry>()
                : new[] { new RemoteEntry { Name = "[WFTP] - ( 1M 1F - COMPLETE ) - [WFTP]", Type = "dir" } }).ToList();
        var root = Path.Combine(Path.GetTempPath(), "weave-recheck-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new JsonStore(root);
        var finished = DateTime.UtcNow.AddMinutes(-5);
        var request = new TransferRequest { Race = true, ViaApi = true, FromSite = "source", ToSite = "target",
            SourcePath = "/release", DestPath = "/release", MeshSites = mesh ? new() { "source", "target" } : new() };
        store.UpsertJob(new Job { Id = "failed", Type = JobType.Race, State = JobState.Failed, Request = request,
            CreatedAt = finished.AddMinutes(-2), StartedAt = finished.AddMinutes(-2), FinishedAt = finished,
            Error = "destination remains occupied; automatic retry suppressed", FilesCovered = 1, FilesTotal = 2,
            FilesDone = 1, BytesDone = 24, CumulativeBytes = 24,
            SfvFiles = new() { new() { Name = "payload.r00", Sfv = "release.sfv", Crc = "00000000", Size = 64, Status = "waiting" } },
            Files = new() { new() { Name = "release.sfv", Status = "done", Size = 24 },
                new() { Name = "payload.r00", Status = "fail", Error = "busy", Size = 64 } },
            Events = new() { new() { Time = finished, Level = "error", Message = "original failed evidence" } } });
        // Upsert/save synchronously initializes this new engine's persisted state.
        typeof(JsonStore).GetMethod("FlushLocked", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(store, new object[] { Enum.ToObject(typeof(JsonStore).GetNestedType("StateSections", System.Reflection.BindingFlags.NonPublic)!, 63) });
        var engine = new WeaveEngine(root);
        var settings = engine.Settings(false);
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var writes = new ConcurrentQueue<string>();
        foreach (var server in new[] { source, target }) server.BeforeCommand = cmd =>
        {
            if (new[] { "STOR ", "APPE ", "MKD ", "DELE ", "RNFR ", "RNTO ", "PORT ", "EPRT " }.Any(cmd.StartsWith)) writes.Enqueue(cmd);
            return Task.CompletedTask;
        };
        if (scenario == "missing") target.Files.TryRemove("payload.r00", out _);
        if (scenario == "partial") target.Files["payload.r00"] = new byte[16];
        if (scenario == "hint") { source.Files["payload.r00"] = new byte[1]; target.Files["payload.r00"] = new byte[1]; }
        if (scenario == "listing") target.Reject = cmd => cmd.StartsWith("STAT ") ? "550 unavailable" : null;
        if (scenario == "sfv") target.Reject = cmd => cmd.StartsWith("RETR ") ? "550 unavailable" : null;
        if (scenario == "truncated") target.Files["release.sfv"] = Encoding.ASCII.GetBytes("other.r00 00000000\r\n");
        if (scenario == "mismatch") target.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 FFFFFFFF\r\n");
        if (scenario == "sample") { source.Files["sample.mkv"] = new byte[64]; target.Files["sample.mkv"] = new byte[16]; }
        if (scenario == "cancelled") { store = (JsonStore)typeof(WeaveEngine).GetField("_store", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
            store.UpdateJob("failed", j => j.State = JobState.Cancelled); }
        if (scenario == "blocked")
        {
            var blocked = target.Site("target", false, false);
            engine.SaveSite("target", blocked);
        }
        if (scenario is "removed" or "changing" or "policy-change" or "config-change")
        {
            var reads = 0;
            target.BeforeCommand = cmd =>
            {
                if (cmd.StartsWith("RETR ") && Interlocked.Increment(ref reads) == 2)
                {
                    if (scenario == "removed") engine.RemoveJob("failed");
                    else if (scenario == "policy-change") engine.SaveSite("target", target.Site("target", false, false));
                    else if (scenario == "config-change")
                    {
                        var changed = target.Site("target", false, true);
                        changed.Host = "127.0.0.2";
                        engine.SaveSite("target", changed);
                    }
                    else target.Files["payload.r00"] = new byte[16];
                }
                return Task.CompletedTask;
            };
        }
        var result = await engine.RecheckRaceAsync("failed");
        var job = engine.Job("failed");
        check(result == (scenario == "complete"), $"fresh stable SFV completion only: {scenario} (mesh={mesh})");
        check(writes.IsEmpty, $"completion recheck never writes or starts FXP: {scenario} (mesh={mesh})");
        if (scenario == "complete")
        {
            check(job!.State == JobState.Succeeded && job.Error == "" && job.FilesCovered == 2 && job.FilesTotal == 2 &&
                job.SfvFiles.Single().Status == "covered", $"failed race and stale manifest recover from new coverage (mesh={mesh})");
            check(job.FilesDone == 1 && job.BytesDone == 24 && job.CumulativeBytes == 24 && !job.SfvFiles.Single().UploadedByUs &&
                job.Files.Any(f => f.Status == "fail") && job.FinishedAt == finished && job.Events.Any(e => e.Message == "original failed evidence"),
                $"reconciliation preserves our contribution, original timing and failure evidence (mesh={mesh})");
            check(!await engine.RecheckRaceAsync("failed"), $"successful recheck is idempotent (mesh={mesh})");
        }
        else check(scenario == "removed" ? job is null : job!.State == (scenario == "cancelled" ? JobState.Cancelled : JobState.Failed),
            $"incomplete, removed or stopped job is never promoted: {scenario} (mesh={mesh})");
    }

    private static async Task LateOpponent(Action<bool, string> check, bool mesh, string outcome)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[64];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        target.Files["payload.r00"] = new byte[16];
        target.Reject = cmd => cmd == "PRET STOR payload.r00" ? "553 file is being uploaded by another user" : null;
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-late-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25; settings.RaceMaxIdleCycles = 61;
        settings.MaxConcurrentRaceJobs = 1;
        settings.IncompleteRaceRetries = 2; settings.RaceCompletionRecheckSeconds = outcome == "expire" ? 1 : 12; settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false)); engine.AddSite(target.Site("target", false, true));
        var job = engine.StartFxp(new TransferRequest { Race = true, ViaApi = true, FromSite = "source", ToSite = "target",
            SourcePath = "/release", DestPath = "/release", MeshSites = mesh ? new() { "source", "target" } : new() });
        var deadline = DateTime.UtcNow.AddSeconds(35);
        try
        {
            while (!engine.Job(job.Id)!.Events.Any(e => e.Message.StartsWith("read-only completion recheck for")))
            {
                if (DateTime.UtcNow > deadline) throw new Exception("read-only phase was not reached");
                await Task.Delay(50);
            }
            check(!engine.Job(job.Id)!.Terminal && engine.Job(job.Id)!.Slots.Count == 0,
                $"occupied race releases transfer workers and waits read-only (mesh={mesh})");
            var fresh = engine.StartFxp(new TransferRequest { Race = true, DryRun = true,
                FromSite = "source", ToSite = "target", SourcePath = "/fresh", DestPath = "/fresh" });
            var freshDeadline = DateTime.UtcNow.AddSeconds(2);
            while (!engine.Job(fresh.Id)!.Terminal && DateTime.UtcNow < freshDeadline) await Task.Delay(20);
            check(engine.Job(fresh.Id)!.State == JobState.Succeeded,
                $"read-only completion waiting leaves the configured worker slot free for new races (mesh={mesh})");
            var verificationStarted = DateTime.UtcNow;
            if (outcome == "complete")
            {
                target.Files["payload.r00"] = new byte[64];
                target.Listing = _ => target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length })
                    .Append(new RemoteEntry { Name = "[WFTP] - ( 1M 1F - COMPLETE ) - [WFTP]", Type = "dir" }).ToList();
            }
            else if (outcome == "cancel") engine.CancelJob(job.Id);
            while (!engine.Job(job.Id)!.Terminal && DateTime.UtcNow < deadline) await Task.Delay(50);
            var done = engine.Job(job.Id)!;
            check(done.State == (outcome == "complete" ? JobState.Succeeded : outcome == "cancel" ? JobState.Cancelled : JobState.Failed) && target.Rejections == 1,
                $"bounded read-only phase respects {outcome} without repeated STOR (mesh={mesh})");
            if (outcome != "complete") check(DateTime.UtcNow - verificationStarted < TimeSpan.FromSeconds(6),
                $"read-only phase ends promptly on {outcome} (mesh={mesh})");
            check(!done.Files.Any(f => f.Name == "payload.r00" && f.Status == "done") && !done.SfvFiles.Single().UploadedByUs,
                $"late completion never credits opponent's payload as our upload (mesh={mesh})");
        }
        finally { if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id); }
    }
}
