using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;
using System.Text;

internal static class FxpFailureTests
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var targetReply in new[] { "553 payload.r00: File exists.",
            "553 payload.r00: file is being uploaded by another user", "426 destination disk failure", "226 transfer complete" })
            await FinalReplies(check, "426 Download failed: source upload failed: aborted: session disconnected", targetReply);
        await FinalReplies(check, "226 transfer complete", "426 destination disk failure");
        await FinalReplies(check, "226 transfer complete", "226 transfer complete");
        await MeshCollision(check, false);
        await MeshCollision(check, true);
    }

    private static async Task FinalReplies(Action<bool, string> check, string sourceReply, string targetReply)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        source.TransferFinalReply = _ => sourceReply;
        target.TransferFinalReply = _ => targetReply;
        using var src = await FtpClient.DialAndLoginAsync(FtpClient.Config.FromSite(source.Site("source", true, false)));
        var targetConfig = FtpClient.Config.FromSite(target.Site("destination", false, true));
        using var dst = await FtpClient.DialAndLoginAsync(targetConfig);
        var messages = new List<string>();
        Exception? failure = null;
        try
        {
            await FxpTransfer.TransferSingleAsync(src, dst, targetConfig, "/release/payload.r00", "/release/payload.r00",
                (_, message) => messages.Add(message), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) { failure = ex; }
        var label = $"source={sourceReply[..3]}, destination={targetReply}";
        var success = sourceReply.StartsWith("226") && targetReply.StartsWith("226");
        check((failure is null) == success, "both peers must finish successfully: " + label);
        if (success)
        {
            check((await src.CommandAsync("NOOP")).code == 200 && (await dst.CommandAsync("NOOP")).code == 200,
                "successful final replies leave both pooled connections synchronized");
            return;
        }
        check(FxpTransfer.IsDestinationDupeError(failure!) == targetReply.Contains("File exists"),
            "destination dupe survives a simultaneous source 426: " + label);
        check(FxpTransfer.IsDestinationBusyError(failure!) == targetReply.Contains("being uploaded"),
            "destination occupancy survives a simultaneous source 426: " + label);
        check(FxpTransfer.RequiresConnectionDrop(failure!), "failed final replies cannot leak into pooled commands: " + label);
        if (!sourceReply.StartsWith("226"))
            check(messages.Any(m => m.Contains("source") && m.Contains(sourceReply)), "source failure remains visible: " + label);
        if (!targetReply.StartsWith("226"))
            check(messages.Any(m => m.Contains("destination") && m.Contains(targetReply)), "destination failure remains visible: " + label);
    }

    private static async Task MeshCollision(Action<bool, string> check, bool busy)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        var published = 0;
        var uploads = 0;
        source.TransferFinalReply = command => command == "RETR payload.r00" ? "426 source upload failed: aborted" : null;
        target.TransferFinalReply = command => command == "STOR payload.r00"
            ? busy ? "553 payload.r00: file is being uploaded by another user" : "553 payload.r00: File exists." : null;
        target.BeforeCommand = command =>
        {
            if (command == "STOR payload.r00") Interlocked.Increment(ref uploads);
            return Task.CompletedTask;
        };
        target.Listing = _ => target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file",
            Size = f.Key == "payload.r00" && Volatile.Read(ref published) == 0 ? 1 : f.Value.Length }).ToList();
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-failure-tests-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 10000;
        settings.IncompleteRaceRetries = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("destination", false, true));
        var job = engine.StartFxp(new TransferRequest { Race = true, FromSite = "source", ToSite = "destination",
            SourcePath = "/release", DestPath = "/release", ViaApi = true, MeshSites = new() { "source", "destination" } });
        async Task Until(Func<bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (!predicate())
            {
                if (DateTime.UtcNow > deadline) throw new Exception("Final reply mesh timeout: " + string.Join("\n", engine.Job(job.Id)!.Events.Select(e => e.Message)));
                await Task.Delay(20);
            }
        }
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "payload.r00" && f.Status == "dupe"));
            await Until(() => engine.Job(job.Id)!.Events.Any(e => e.Message.Contains("payload.r00 expected=4096") &&
                e.Message.Contains("destination=1 (occupied)")));
            var pending = engine.Job(job.Id)!;
            check(uploads == 1 && pending.Files.Count(f => f.Name == "payload.r00") == 1,
                $"mesh preserves destination occupancy after source 426 without repeated attempts (busy={busy})");
            check(!pending.Terminal && !pending.Files.Any(f => f.Name == "payload.r00" && f.Status == "done"),
                $"source 426 and occupied partial target do not falsely complete the race (busy={busy})");
            Volatile.Write(ref published, 1);
            await Until(() => engine.Job(job.Id)!.Terminal);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && uploads == 1,
                $"fresh matching listing completes the mesh without another STOR (busy={busy})");
        }
        finally
        {
            if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal);
        }
    }
}
