using System.Text;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RaceManifestTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "weave-manifest-" + Guid.NewGuid().ToString("N"));
        var store = new JsonStore(Path.Combine(root, "state.json"));
        store.UpsertJob(new Job { Id = "manifest", Type = JobType.Race, FilesTotal = 620,
            Files = new() { new() { Name = "Disc 1/older.rar", Status = "done", Size = 4096 },
                new() { Name = "Disc 1/peer.rar", Status = "dupe" }, new() { Name = "Disc 1/failed.rar", Status = "fail" } } });
        var manifest = new RaceManifestPublisher(store, "manifest");
        manifest.Uploaded("Disc 1/early.rar", 4096);
        manifest.Register("Disc 1/release.sfv", "Disc 1", Sfv.Parse(
            "older.rar C71C0011\npeer.rar C71C0011\nfailed.rar C71C0011\nearly.rar C71C0011\n" +
            string.Join('\n', Enumerable.Range(0, 616).Select(n => $"payload.{n:D4} C71C0011"))));
        manifest.Register("Disc 1/release.sfv", "Disc 1", Sfv.Parse("PEER.RAR C71C0011"));
        var job = store.Job("manifest")!;
        check(job.SfvFiles.Count == 620 && job.SfvFiles.All(f => f.Name.StartsWith("Disc 1/")),
            "complete SFV inventory preserves subdirectory names and deduplicates case-insensitively");
        check(job.SfvFiles.Single(f => f.Name.EndsWith("early.rar")).UploadedByUs &&
            job.SfvFiles.Single(f => f.Name.EndsWith("older.rar")).UploadedByUs,
            "confirmed uploads before SFV parsing and previous retry history retain own-upload credit");
        check(!job.SfvFiles.Single(f => f.Name.EndsWith("peer.rar")).UploadedByUs &&
            !job.SfvFiles.Single(f => f.Name.EndsWith("failed.rar")).UploadedByUs,
            "dupe and failed attempts never claim an own upload");
        manifest.Uploaded("Disc 1/failed.rar", 1);
        check(!store.Job("manifest")!.SfvFiles.Single(f => f.Name.EndsWith("failed.rar")).UploadedByUs,
            "a successful one-byte transfer contradicted by SFV is not credited as our complete file");
        manifest.Publish(new[] { new RaceManifestObservation("Disc 1/peer.rar", 4096, true, true),
            new RaceManifestObservation("Disc 1/failed.rar", 4096, false, true),
            new RaceManifestObservation("Disc 1/payload.0000", null, false, false) });
        job = store.Job("manifest")!;
        check(job.SfvFiles.Single(f => f.Name.EndsWith("peer.rar")).Status == "covered" &&
            job.SfvFiles.Single(f => f.Name.EndsWith("failed.rar")).Status == "waiting" &&
            job.SfvFiles.Single(f => f.Name.EndsWith("payload.0000")).Status == "missing",
            "SFV inventory distinguishes complete, waiting and genuinely missing files");
        manifest.Publish(new[] { new RaceManifestObservation("Disc 1/peer.rar", 4096, null, false) });
        check(store.Job("manifest")!.SfvFiles.Single(f => f.Name.EndsWith("peer.rar")).Status == "unknown",
            "failed or unavailable listings do not invent missing or complete status");
        manifest.Publish(new[] { new RaceManifestObservation("Disc 1/early.rar", 8192, false, true) });
        check(!store.Job("manifest")!.SfvFiles.Single(f => f.Name.EndsWith("early.rar")).UploadedByUs,
            "our earlier smaller transfer is not credited as a later larger file");
        var details = store.JobsForUi(new[] { "manifest" }).Single();
        check(details.SfvFiles.Count == 620 && store.JobsForUi().Single().SfvFiles.Count == 0,
            "expanded UI receives every SFV file beyond 500 while collapsed summaries stay small");
        details.SfvFiles[0].Crc = "mutated";
        check(store.Job("manifest")!.SfvFiles[0].Crc == "C71C0011" && store.Job("manifest")!.FilesDone == 0,
            "UI snapshots cannot mutate manifest data or inflate transfer counters");
        var archive = new JobArchiveStore(Path.Combine(root, "manifest-archive.db"));
        job = store.Job("manifest")!;
        archive.Archive(new[] { (job, JsonSerializer.Serialize(job)) });
        var restored = JsonSerializer.Deserialize<Job>(archive.Payload("manifest")!)!;
        check(restored.SfvFiles.Count == 620 && restored.SfvFiles.Any(f => f.UploadedByUs),
            "complete manifest and own-upload evidence survive SQLite history serialization");
        check(JsonSerializer.Deserialize<Job>("{}")!.SfvFiles.Count == 0,
            "legacy history without saved SFV entries remains compatible without invented filenames");
        foreach (var mesh in new[] { false, true }) await Race(check, mesh);
    }

    private static async Task Race(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["ours.rar"] = new byte[4096];
        source.Files["peer.rar"] = new byte[128];
        target.Files["peer.rar"] = new byte[128];
        target.Reject = command => command == "PRET STOR peer.rar" ? "553 peer.rar: File exists." : null;
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("ours.rar C71C0011\npeer.rar C2A8FA9D\nunseen.rar C2A8FA9D\n");
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-manifest-race-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.GlobalSkiplist.Clear(); settings.RacePollIntervalMs = 25;
        settings.IncompleteRaceRetries = 0; settings.RaceMaxIdleCycles = 10000;
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        var job = engine.StartFxp(new TransferRequest { Race = true, ViaApi = true,
            FromSite = "source", ToSite = "target", SourcePath = "/release", DestPath = "/release",
            MeshSites = mesh ? new() { "source", "target" } : new() });
        try
        {
            await Wait(() => engine.Job(job.Id)!.SfvFiles.Count == 3 &&
                engine.Job(job.Id)!.SfvFiles.Any(f => f.Name == "ours.rar" && f.UploadedByUs) &&
                engine.Job(job.Id)!.SfvFiles.Any(f => f.Name == "unseen.rar" && f.Status == "missing"));
            var snapshot = engine.Job(job.Id)!;
            check(snapshot.SfvFiles.Count == 3 && snapshot.SfvFiles.Single(f => f.Name == "unseen.rar").Status == "missing",
                $"SFV files never seen or attempted appear in live inventory (mesh={mesh})");
            check(snapshot.SfvFiles.Single(f => f.Name == "ours.rar").UploadedByUs &&
                snapshot.SfvFiles.Single(f => f.Name == "peer.rar").Status == "covered" &&
                !snapshot.SfvFiles.Single(f => f.Name == "peer.rar").UploadedByUs,
                $"live inventory separates own successful uploads from peer-covered files (mesh={mesh})");
            target.Files["unseen.rar"] = new byte[128];
            await Wait(() => engine.Job(job.Id)!.Terminal);
            snapshot = engine.Job(job.Id)!;
            check(snapshot.State == JobState.Succeeded && snapshot.SfvFiles.All(f => f.Status == "covered") &&
                !snapshot.SfvFiles.Single(f => f.Name == "unseen.rar").UploadedByUs,
                $"fresh peer arrival updates final inventory without giving us upload credit (mesh={mesh})");
        }
        finally
        {
            if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
            await Wait(() => engine.Job(job.Id)!.Terminal);
        }
    }

    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!condition() && DateTime.UtcNow < until) await Task.Delay(20);
    }
}
