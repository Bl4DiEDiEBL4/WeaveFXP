using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RaceRepairTests
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var precheck in new[] { false, true })
        {
            await Repair(check, precheck, mesh: false, delete: true, grow: true);
            await Repair(check, precheck, mesh: false, delete: true, grow: false);
            await Repair(check, precheck, mesh: false, delete: false, grow: true);
        }
        await Repair(check, false, mesh: true, delete: true, grow: true);
        await Repair(check, false, mesh: true, delete: true, grow: false);
        await Repair(check, false, mesh: true, delete: false, grow: true);
        foreach (var mesh in new[] { false, true })
        {
            await VerificationFailure(check, mesh, missing: true);
            await VerificationFailure(check, mesh, missing: false);
            await VerificationFailure(check, mesh, missing: false, nested: true);
            await Repair(check, false, mesh, delete: true, grow: false, duringSfv: true);
            await StaleListing(check, mesh);
            await DelayedRepair(check, mesh);
            await StatusDirectory(check, mesh);
            await Repair(check, false, mesh, delete: true, grow: false, shrink: true);
            await Repair(check, false, mesh, delete: false, grow: false, shrink: true);
        }
    }

    private static async Task Repair(Action<bool, string> check, bool precheck, bool mesh, bool delete, bool grow, bool duringSfv = false, bool shrink = false)
    {
        await using var source = new TransferServer();
        await using var destination = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        var path = Path.Combine(Path.GetTempPath(), "weave-race-repair-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 600;
        settings.RaceDestinationPrecheck = precheck;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(destination.Site("destination", false, true));
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "source", ToSite = "destination",
            SourcePath = "/release", DestPath = "/release", ViaApi = true,
            MeshSites = mesh ? new() { "source", "destination" } : new()
        });
        var mode = $"mesh={mesh}, precheck={precheck}, delete={delete}, grow={grow}, duringSfv={duringSfv}, shrink={shrink}";
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "payload.r00" && f.Status == "done"), engine, job.Id);
            // A late SFV/CRC check removes an upload that previously received 226.
            var repaired = Enumerable.Repeat((byte)7, grow ? 64 : shrink ? 16 : 32).ToArray();
            if (duringSfv)
            {
                var triggered = 0;
                source.BeforeCommand = command =>
                {
                    if (command.StartsWith("RETR ") && command.EndsWith("release.sfv") && Interlocked.Exchange(ref triggered, 1) == 0)
                    {
                        destination.Files.TryRemove("payload.r00", out _);
                        source.Files["payload.r00"] = repaired;
                    }
                    return Task.CompletedTask;
                };
            }
            else
            {
                if (delete) destination.Files.TryRemove("payload.r00", out _);
                source.Files["payload.r00"] = repaired;
            }
            // Publish the SFV only after the source replacement. The race must not
            // finish from the earlier successful transfer's cached presence/size.
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded, $"repaired race completes ({mode})");
            check(destination.Files.TryGetValue("payload.r00", out var actual) && actual.SequenceEqual(repaired),
                $"successful race actually contains the repaired source bytes ({mode})");
            check(engine.Job(job.Id)!.Files.Count(f => f.Name == "payload.r00" && f.Status == "done") >= 2,
                $"vanished/changed file was transferred again ({mode})");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static (WeaveEngine Engine, Job Job) Start(TransferServer source, TransferServer destination, bool mesh)
    {
        var path = Path.Combine(Path.GetTempPath(), "weave-race-verification-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 600;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(destination.Site("destination", false, true));
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "source", ToSite = "destination", SourcePath = "/release", DestPath = "/release",
            MeshSites = mesh ? new() { "source", "destination" } : new(), ViaApi = true
        });
        return (engine, job);
    }

    private static async Task VerificationFailure(Action<bool, string> check, bool mesh, bool missing, bool nested = false)
    {
        await using var source = new TransferServer();
        await using var destination = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        if (nested)
            foreach (var server in new[] { source, destination })
                server.Listing = path => path.TrimEnd('/').EndsWith("/CD1")
                    ? server.Files.Where(f => f.Key == "payload.r00").Select(f => new RemoteEntry { Name = f.Key, Size = f.Value.Length, Type = "file" }).ToList()
                    : new List<RemoteEntry> { new() { Name = "CD1", Type = "dir" } }
                        .Concat(server.Files.Where(f => f.Key.EndsWith(".sfv")).Select(f => new RemoteEntry { Name = f.Key, Size = f.Value.Length, Type = "file" })).ToList();
        var (engine, job) = Start(source, destination, mesh);
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name.EndsWith("payload.r00") && f.Status == "done"), engine, job.Id);
            if (missing)
            {
                destination.Reject = command => command.Contains("STOR") && command.EndsWith("payload.r00") ? "550 write denied" : null;
                destination.Files.TryRemove("payload.r00", out _);
            }
            else
                destination.Reject = command => (nested ? command.StartsWith("CWD ") && command.EndsWith("/CD1") : command.StartsWith("STAT ")) ? "550 temporarily unavailable" : null;
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes((nested ? "CD1/" : "") + "payload.r00 00000000\r\n");
            await Task.Delay(900);
            check(destination.Rejections > 0, $"test injected a real FTP failure (mesh={mesh}, nested={nested})");
            check(engine.Job(job.Id)!.State != JobState.Succeeded,
                $"race cannot succeed while {(missing ? "repair is denied" : "destination verification fails")} (mesh={mesh}, nested={nested})");
            if (!missing)
                check(engine.Job(job.Id)!.Files.Count(f => f.Name.EndsWith("payload.r00") && f.Status == "done") == 1,
                    $"failed listing does not erase confirmed presence and cause duplicate transfers (mesh={mesh}, nested={nested})");
            destination.Reject = null;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && destination.Files.ContainsKey("payload.r00"),
                $"race recovers when {(missing ? "uploads" : "listings")} work again (mesh={mesh}, nested={nested})");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task StatusDirectory(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var destination = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        foreach (var server in new[] { source, destination })
        {
            server.Listing = _ => new List<RemoteEntry> { new() { Name = "COMPLETE", Type = "dir" } }
                .Concat(server.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length })).ToList();
            server.Reject = command => command.StartsWith("CWD ") && command.EndsWith("/COMPLETE") ? "550 virtual status directory" : null;
        }
        var (engine, job) = Start(source, destination, mesh);
        try
        {
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && destination.Files.ContainsKey("payload.r00"),
                $"virtual complete marker does not block verified race completion (mesh={mesh})");
            check(source.Rejections == 0 && destination.Rejections == 0,
                $"status directories are not traversed as release subfolders (mesh={mesh})");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task DelayedRepair(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var destination = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        var (engine, job) = Start(source, destination, mesh);
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "payload.r00" && f.Status == "done"), engine, job.Id);
            source.Files.TryRemove("payload.r00", out _);
            destination.Files.TryRemove("payload.r00", out _);
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
            await Task.Delay(900);
            check(engine.Job(job.Id)!.State == JobState.Running && !destination.Files.ContainsKey("payload.r00"),
                $"race waits for another racer to repair the source (mesh={mesh})");
            var repaired = Enumerable.Repeat((byte)7, 64).ToArray();
            source.Files["payload.r00"] = repaired;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && destination.Files["payload.r00"].SequenceEqual(repaired),
                $"later source repair is discovered and copied (mesh={mesh})");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task StaleListing(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var destination = new TransferServer();
        source.Files["payload.r00"] = new byte[32];
        var (engine, job) = Start(source, destination, mesh);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "payload.r00" && f.Status == "done"), engine, job.Id);
            var pendingTransfer = 0;
            var blockedListing = 0;
            destination.BeforeCommand = async command =>
            {
                if (command == "STOR next.r00")
                {
                    Interlocked.Exchange(ref pendingTransfer, 1);
                    await captured.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            };
            destination.AfterListingSnapshot = async () =>
            {
                if (Volatile.Read(ref pendingTransfer) == 1 && Interlocked.Exchange(ref blockedListing, 1) == 0)
                {
                    captured.TrySetResult();
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            };
            source.Files["next.r00"] = new byte[64];
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "next.r00" && f.Status == "done"), engine, job.Id);
            check(captured.Task.IsCompleted, $"test captured a LIST before transfer completion (mesh={mesh})");
            release.TrySetResult();
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\nnext.r00 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && destination.Files["next.r00"].Length == 64,
                $"stale in-flight LIST cannot undo a newer successful transfer (mesh={mesh})");
            check(engine.Job(job.Id)!.Files.Count(f => f.Name == "next.r00" && f.Status == "done") == 1,
                $"overlapping old LIST causes no duplicate upload (mesh={mesh})");
        }
        finally
        {
            captured.TrySetResult();
            release.TrySetResult();
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, string id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Exception("Race repair timed out: " + string.Join("\n", engine.Job(id)!.Events.Select(e => e.Message)));
            await Task.Delay(20);
        }
    }
}
