using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RoutePolicyTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await LiveChange(check, threeSites: false);
        await LiveChange(check, threeSites: true);
        await BlockDuringSetup(check, mesh: true);
        await BlockDuringSetup(check, mesh: false);
        foreach (var policy in new[] { "target-exception", "source-exception", "target-default", "source-default",
                     "no-source", "no-destination", "no-download", "no-upload" })
            await InitiallyBlocked(check, policy);
    }

    private static WeaveEngine Engine(TransferServer hv, TransferServer kpn)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-route-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 1000;
        settings.IncompleteRaceRetries = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(hv.Site("HV", true, true));
        engine.AddSite(kpn.Site("KPN", true, true));
        return engine;
    }

    private static Job Start(WeaveEngine engine) => engine.StartSpread(new SpreadRequest
    {
        Race = true, FromSite = "HV", ToSites = new() { "KPN" },
        SourcePath = "/release", DestPath = "/release", ViaApi = true
    }).Jobs.Single();

    private static async Task LiveChange(Action<bool, string> check, bool threeSites)
    {
        await using var hv = new TransferServer();
        await using var kpn = new TransferServer();
        await using var third = new TransferServer();
        var sfv = Encoding.ASCII.GetBytes("first.r00 00000000\r\nblocked.r00 00000000\r\nallowed.r00 00000000\r\nlater.r00 00000000\r\n");
        hv.Files["release.sfv"] = sfv;
        kpn.Files["release.sfv"] = sfv;
        hv.Files["first.r00"] = new byte[64];
        var forbidden = 0;
        hv.BeforeCommand = command =>
        {
            if (command == "PRET STOR blocked.r00") Interlocked.Increment(ref forbidden);
            return Task.CompletedTask;
        };
        var engine = Engine(hv, kpn);
        if (threeSites) engine.AddSite(third.Site("third", false, true));
        var job = threeSites ? engine.StartSpread(new SpreadRequest
        {
            Race = true, FromSite = "HV", ToSites = new() { "KPN", "third" },
            SourcePath = "/release", DestPath = "/release", ViaApi = true
        }).Jobs.Single() : Start(engine);
        try
        {
            await Until(() => kpn.Files.ContainsKey("first.r00"));
            var site = engine.Site("KPN")!;
            site.ExceptTargetSites.Add("hV");
            engine.SaveSite("KPN", site);
            kpn.Files["blocked.r00"] = new byte[64];
            hv.Files["allowed.r00"] = new byte[64];
            await Until(() => kpn.Files.ContainsKey("allowed.r00"));
            await Task.Delay(700);
            check(Volatile.Read(ref forbidden) == 0 && !hv.Files.ContainsKey("blocked.r00"),
                $"blocking KPN -> HV during a running {(threeSites ? 3 : 2)}-site mesh prevents new FTP attempts");
            check(engine.Job(job.Id)!.Files.Any(f => f.Name == "allowed.r00" && f.FromSite == "HV" && f.ToSite == "KPN"),
                "blocking KPN -> HV leaves HV -> KPN running");

            site.ExceptTargetSites.Clear();
            engine.SaveSite("KPN", site);
            await Until(() => hv.Files.ContainsKey("blocked.r00"));
            check(Volatile.Read(ref forbidden) == 1, "unblocking a live route refreshes existing scoreboard candidates exactly once");
        }
        finally { engine.CancelJob(job.Id); await Until(() => engine.Job(job.Id)!.Terminal); }
    }

    private static async Task BlockDuringSetup(Action<bool, string> check, bool mesh)
    {
        await using var hv = new TransferServer();
        await using var kpn = new TransferServer();
        kpn.Files["blocked.r00"] = new byte[64];
        var setup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forbidden = 0;
        hv.BeforeCommand = async command =>
        {
            if (command.StartsWith("MKD "))
            {
                setup.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            if (command.StartsWith("PRET STOR ") || command.StartsWith("STOR ")) Interlocked.Increment(ref forbidden);
        };
        var engine = Engine(hv, kpn);
        var job = engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "KPN", ToSite = "HV", SourcePath = "/release", DestPath = "/release", ViaApi = true,
            MeshSites = mesh ? new() { "KPN", "HV" } : new()
        });
        try
        {
            await setup.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var site = engine.Site("HV")!;
            site.ExceptSourceSites.Add("KPN");
            engine.SaveSite("HV", site);
            release.TrySetResult();
            if (mesh) await Task.Delay(700);
            else await Until(() => engine.Job(job.Id)!.Terminal);
            check(Volatile.Read(ref forbidden) == 0 && !hv.Files.ContainsKey("blocked.r00"),
                $"{(mesh ? "mesh" : "directional")} race rechecks a reserved route after destination setup before PRET/STOR");
            if (!mesh) check(engine.Job(job.Id)!.State == JobState.Failed && engine.Job(job.Id)!.Events.Any(e => e.Message.Contains("blocked by current site")),
                "directional race stops with an explicit blocked-route reason without retries");
        }
        finally
        {
            release.TrySetResult();
            engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal);
        }
    }

    private static async Task InitiallyBlocked(Action<bool, string> check, string policy)
    {
        await using var hv = new TransferServer();
        await using var kpn = new TransferServer();
        var sfv = Encoding.ASCII.GetBytes("allowed.r00 00000000\r\nblocked.r00 00000000\r\nlater.r00 00000000\r\n");
        hv.Files["release.sfv"] = sfv;
        kpn.Files["release.sfv"] = sfv;
        hv.Files["allowed.r00"] = new byte[64];
        kpn.Files["blocked.r00"] = new byte[64];
        var forbidden = 0;
        hv.BeforeCommand = command =>
        {
            if (command.StartsWith("PRET STOR ")) Interlocked.Increment(ref forbidden);
            return Task.CompletedTask;
        };
        var engine = Engine(hv, kpn);
        var src = engine.Site("KPN")!;
        var dst = engine.Site("HV")!;
        switch (policy)
        {
            case "target-exception": src.ExceptTargetSites.Add("hv"); break;
            case "source-exception": dst.ExceptSourceSites.Add("kpn"); break;
            case "target-default": src.TransferTargetPolicy = SiteTransferPolicy.Block; break;
            case "source-default": dst.TransferSourcePolicy = SiteTransferPolicy.Block; break;
            case "no-source": src.BlockTransferFrom = true; break;
            case "no-destination": dst.BlockTransferTo = true; break;
            case "no-download": src.AllowDownload = false; break;
            case "no-upload": dst.AllowUpload = false; break;
        }
        engine.SaveSite("KPN", src);
        engine.SaveSite("HV", dst);
        check(!engine.TransferAllowed(src, dst) && engine.TransferAllowed(dst, src),
            $"route display agrees with the scheduler for {policy}");
        var job = Start(engine);
        try
        {
            await Until(() => kpn.Files.ContainsKey("allowed.r00"));
            await Task.Delay(300);
            check(Volatile.Read(ref forbidden) == 0 && !hv.Files.ContainsKey("blocked.r00"),
                $"two-site scoreboard respects initial {policy} without blocking the reverse route");
            foreach (var race in new[] { false, true })
            {
                // Upload/download switches govern racing; manual FXP keeps its existing semantics.
                if (!race && policy is "no-download" or "no-upload") continue;
                var refused = false;
                Job? direct = null;
                try { direct = engine.StartFxp(new TransferRequest { Race = race, FromSite = "KPN", ToSite = "HV", SourcePath = "/release", DestPath = "/release", ViaApi = true }); }
                catch (IOException) { refused = true; }
                finally { if (direct is not null) { engine.CancelJob(direct.Id); await Until(() => engine.Job(direct.Id)!.Terminal); } }
                check(refused, $"direct {(race ? "race" : "FXP")} rejects {policy} before connecting");
            }
        }
        finally { engine.CancelJob(job.Id); await Until(() => engine.Job(job.Id)!.Terminal); }
    }

    private static async Task Until(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (!ready())
        {
            if (DateTime.UtcNow >= end) throw new Exception("Route policy test timed out");
            await Task.Delay(20);
        }
    }
}
