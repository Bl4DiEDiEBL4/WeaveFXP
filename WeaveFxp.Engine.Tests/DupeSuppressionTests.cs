using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class DupeSuppressionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var checkLock = new object();
        void Verify(bool ok, string name) { lock (checkLock) check(ok, name); }
        await Task.WhenAll(new[] { "larger", "busy", "unlisted", "bidirectional", "stalled" }.Select(mode => Sample(Verify, mode)));
    }

    private static async Task Sample(Action<bool, string> check, string mode)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        const string sample = "release.sample.mkv";
        var payload = Enumerable.Repeat((byte)7, 64).ToArray();
        source.Files[sample] = payload;
        var sfv = Encoding.ASCII.GetBytes($"Sample/{sample} 00000000\r\n");
        foreach (var server in new[] { source, target })
        {
            server.Files["release.sfv"] = sfv;
            server.Listing = path => path.TrimEnd('/').EndsWith("/Sample", StringComparison.OrdinalIgnoreCase)
                ? server.Files.Where(f => f.Key != "release.sfv").Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length }).ToList()
                : new() { new() { Name = "Sample", Type = "dir" }, new() { Name = "release.sfv", Type = "file", Size = sfv.Length } };
        }
        if (mode != "unlisted") target.Files[sample] = new byte[mode == "busy" ? 16 : 128];
        target.Reject = command => command == "PRET STOR " + sample
            ? mode == "busy" ? "553 file is being uploaded by another user" : "553 File exists."
            : null;
        if (mode == "bidirectional") source.Reject = target.Reject;
        int Rejections() => target.Rejections + source.Rejections;
        var path = Path.Combine(Path.GetTempPath(), "weave-dupe-suppression-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = mode == "stalled" ? 61 : 10000;
        settings.IncompleteRaceRetries = 0;
        settings.RaceCompletionRecheckSeconds = 0; // This test isolates the worker's occupancy bound.
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, mode == "bidirectional"));
        engine.AddSite(target.Site("destination", mode == "bidirectional", true));
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "source", ToSite = "destination", SourcePath = "/release", DestPath = "/release",
            MeshSites = new() { "source", "destination" }, ViaApi = true
        });
        try
        {
            await Until(() => Rejections() > 0, engine, job.Id);
            // Cross TWO old 15-second expiry windows; a sub-second test missed this loop.
            await Task.Delay(TimeSpan.FromSeconds(32));
            check(Rejections() == 1, $"unchanged nested sample is not retried every fifteen seconds ({mode}; attempts={Rejections()})");
            check(engine.Job(job.Id)!.Files.Count(f => f.Name == "Sample/" + sample && f.Status == "dupe") == 1,
                $"unchanged sample produces one dupe history row ({mode})");
            check(engine.Job(job.Id)!.State != JobState.Succeeded, $"occupancy without matching bytes is not completion ({mode})");

            if (mode == "stalled")
            {
                check(engine.Job(job.Id)!.State == JobState.Failed, "unchanged occupancy respects idle termination instead of running forever");
                return;
            }

            if (mode == "larger")
            {
                // A failed listing is not evidence that the occupied file vanished.
                var rejects = 0;
                target.Reply = command => command.StartsWith("STAT ") && Interlocked.Increment(ref rejects) > 0 ? "550 listing unavailable" : null;
                await Until(() => Volatile.Read(ref rejects) > 0, engine, job.Id);
                check(Rejections() == 1, "failed listing does not reopen occupied sample transfers");
                target.Files.TryRemove(sample, out _);
                target.Reject = null;
                target.Reply = null;
            }
            else target.Files[sample] = payload;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files[sample].SequenceEqual(payload),
                $"fresh listing evidence resolves occupied sample ({mode})");
            if (mode == "larger")
                check(engine.Job(job.Id)!.Files.Any(f => f.Name == "Sample/" + sample && f.Status == "done"),
                    "fresh disappearance of a previously listed sample permits repair");
            else check(Rejections() == 1, $"externally completed sample needs no second STOR ({mode})");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Exception("Dupe regression timed out: " + string.Join("\n", engine.Job(id)!.Events.TakeLast(15).Select(e => e.Message)));
            await Task.Delay(20);
        }
    }
}
