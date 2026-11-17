using System.Diagnostics;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

internal static class CbftpCoreTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await Prepared(check);
        await AnnounceCoalescing(check);
        await DownloadOnly(check);
        await PeerFinalWait(check, "source-error");
        await PeerFinalWait(check, "target-error");
        await PeerFinalWait(check, "success");
        await PeerFinalWait(check, "cancel");
        foreach (var mesh in new[] { false, true }) await LiveProgress(check, mesh);
        await NestedPolling(check);
    }

    private static WeaveEngine Engine(TransferServer source, TransferServer target)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-cbftp-tests-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 10000;
        settings.IncompleteRaceRetries = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        return engine;
    }

    private static SpreadRequest Request() => new() { FromSite = "source", ToSites = new() { "target" },
        SourcePath = "/release", DestPath = "/release", Race = true, ViaApi = true, Label = "prepared-release" };

    private static async Task Prepared(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[1024];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        var directoryOrDataCommands = 0;
        foreach (var fixture in new[] { source, target }) fixture.BeforeCommand = cmd =>
        {
            if (new[] { "CWD ", "STAT ", "LIST", "PRET ", "RETR ", "STOR ", "MKD " }.Any(cmd.StartsWith))
                Interlocked.Increment(ref directoryOrDataCommands);
            return Task.CompletedTask;
        };
        var engine = Engine(source, target);
        var req = Request();
        var prepared = engine.PrepareSpread(req, ttlSeconds: 30);
        req.SourcePath = "/mutated";
        prepared.Request.ToSites.Clear();
        await Until(() => engine.PreparedSpreads().Single().WarmupCompleted);
        check(engine.Jobs().Count == 0 && target.Files.Count == 0 && directoryOrDataCommands == 0,
            "prepare only warms login sessions: no race, directories, listings or data commands");
        check(engine.PreparedSpreads().Single().Request.SourcePath == "/release" && engine.PreparedSpreads().Single().Request.ToSites.Count == 1,
            "prepared request is isolated from caller mutations");
        var result = engine.StartPreparedSpread(prepared.Id);
        var job = result.Jobs.Single();
        await Until(() => engine.Job(job.Id)!.Terminal);
        check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].Length == 1024,
            "explicit start executes the prepared race normally");
        var twice = false;
        try { engine.StartPreparedSpread(prepared.Id); } catch (InvalidOperationException) { twice = true; }
        check(twice && engine.PreparedSpreads().Count == 0, "prepared race is consumed atomically and cannot start twice");
        var cancelled = engine.PrepareSpread(Request());
        check(engine.CancelPreparedSpread(cancelled.Id) && !engine.CancelPreparedSpread(cancelled.Id), "prepared race cancellation is idempotent");
        var expired = engine.PrepareSpread(Request(), ttlSeconds: 1);
        await Task.Delay(1100);
        check(engine.PreparedSpreads().Count == 0, "expired prepared races disappear without starting transfers");
        var denied = engine.PrepareSpread(Request());
        var site = engine.Site("target")!;
        site.AllowUpload = false;
        engine.SaveSite("target", site);
        var blocked = false;
        try { engine.StartPreparedSpread(denied.Id); } catch (IOException) { blocked = true; }
        check(blocked, "starting a prepared race revalidates current route permissions");
    }

    private static async Task AnnounceCoalescing(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[1024];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.BeforeCommand = cmd => cmd.StartsWith("STAT") ? hold.Task.WaitAsync(TimeSpan.FromSeconds(10)) : Task.CompletedTask;
        var engine = Engine(source, target);
        var starts = Enumerable.Range(0, 8).Select(_ => Task.Run(() => engine.StartSpread(Request()))).ToArray();
        var batches = await Task.WhenAll(starts);
        var id = batches[0].Jobs.Single().Id;
        try
        {
            check(batches.All(b => b.Jobs.Single().Id == id) && engine.JobCount == 1,
                "concurrent identical API announces share one race and one set of listers");
            var different = Request(); different.DestPath = "/other-release";
            var other = engine.StartSpread(different).Jobs.Single();
            check(other.Id != id, "coalescing preserves distinct target paths");
            engine.CancelJob(other.Id);
            var scoped = Request(); scoped.DownloadOnlySites = new() { "source" };
            var scopeJob = engine.StartSpread(scoped).Jobs.Single();
            check(scopeJob.Id != id, "coalescing preserves distinct per-race route restrictions");
            engine.CancelJob(scopeJob.Id);
            hold.TrySetResult();
            await Until(() => engine.Job(id)!.Terminal);
            check(engine.Job(id)!.State == JobState.Succeeded, "coalesced race still completes its original transfer");
            var next = engine.StartSpread(Request()).Jobs.Single();
            check(next.Id != id, "a new announce after completion can start a fresh race");
            engine.CancelJob(next.Id);
        }
        finally { hold.TrySetResult(); foreach (var job in engine.Jobs().Where(j => !j.Terminal)) engine.CancelJob(job.Id); }
    }

    private static async Task DownloadOnly(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var alternate = new TransferServer();
        await using var target = new TransferServer();
        source.Files["left.r00"] = new byte[128];
        alternate.Files["right.r01"] = new byte[256];
        target.Files["target.r02"] = new byte[64];
        target.Files["release.sfv"] = Encoding.ASCII.GetBytes("left.r00 00000000\r\nright.r01 00000000\r\ntarget.r02 00000000\r\n");
        var engine = Engine(source, target);
        engine.SaveSite("source", source.Site("source", true, true));
        engine.SaveSite("target", target.Site("target", true, true));
        engine.AddSite(alternate.Site("alternate", true, true));
        var request = Request();
        request.ToSites.Add("alternate");
        request.DownloadOnlySites = new() { "SOURCE", "alternate" };
        var prepared = engine.PrepareSpread(request);
        var job = engine.StartPreparedSpread(prepared.Id).Jobs.Single();
        await Until(() => engine.Job(job.Id)!.Terminal);
        var done = engine.Job(job.Id)!;
        check(done.State == JobState.Succeeded && target.Files["left.r00"].Length == 128 && target.Files["right.r01"].Length == 256,
            "per-race download-only sources both feed an allowed target");
        check(done.Files.Where(f => f.Status == "done").All(f => f.ToSite == "target") && source.Files.Count == 1 && alternate.Files.Count == 1,
            "per-race download-only sites never receive files, including through a prepared race");
        check(engine.Site("source")!.AllowUpload && engine.Site("alternate")!.AllowUpload,
            "download-only race scope does not change global site permissions");
        var invalid = Request(); invalid.DownloadOnlySites = new() { "source", "target" };
        var denied = false;
        try { engine.PrepareSpread(invalid); } catch (InvalidOperationException) { denied = true; }
        check(denied, "a prepared race with no permitted destination is rejected");
    }

    private static async Task PeerFinalWait(Action<bool, string> check, string scenario)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[1024];
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (scenario == "source-error") source.TransferFinalReply = _ => "426 source failed";
        if (scenario == "target-error") target.TransferFinalReply = _ => "553 payload.r00: File exists.";
        var delayed = scenario == "target-error" ? source : target;
        delayed.BeforeTransferFinalReply = async _ => { reached.TrySetResult(); await hold.Task.WaitAsync(TimeSpan.FromSeconds(15)); };
        if (scenario == "cancel") source.BeforeTransferFinalReply = delayed.BeforeTransferFinalReply;
        var srcCfg = FtpClient.Config.FromSite(source.Site("source", true, false)); srcCfg.TimeoutSeconds = 30;
        var dstCfg = FtpClient.Config.FromSite(target.Site("target", false, true)); dstCfg.TimeoutSeconds = 30;
        using var src = await FtpClient.DialAndLoginAsync(srcCfg);
        using var dst = await FtpClient.DialAndLoginAsync(dstCfg);
        using var stop = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var transfer = FxpTransfer.TransferSingleAsync(src, dst, dstCfg, "/release/payload.r00", "/release/payload.r00", (_, _) => { }, stop.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (scenario == "success")
            {
                await Task.Delay(5400);
                check(!transfer.IsCompleted, "five-second failure limit does not truncate a healthy delayed final reply");
                hold.TrySetResult();
                await transfer.WaitAsync(TimeSpan.FromSeconds(2));
                check((await src.CommandAsync("NOOP")).code == 200 && (await dst.CommandAsync("NOOP")).code == 200,
                    "healthy delayed completion leaves pooled controls synchronized");
                return;
            }
            if (scenario == "cancel") stop.Cancel();
            Exception? failure = null;
            try { await transfer.WaitAsync(TimeSpan.FromSeconds(7)); } catch (Exception ex) { failure = ex; }
            check(scenario == "cancel" ? failure is OperationCanceledException && watch.Elapsed < TimeSpan.FromSeconds(2)
                : failure is not null && FxpTransfer.RequiresConnectionDrop(failure) && watch.Elapsed < TimeSpan.FromSeconds(6),
                "peer final wait is bounded and cancelled controls are dropped: " + scenario);
            if (scenario == "target-error") check(FxpTransfer.IsDestinationDupeError(failure!), "bounded cleanup preserves destination occupancy classification");
            var disposed = 0;
            foreach (var client in new[] { src, dst }) try { await client.CommandAsync("NOOP"); } catch { disposed++; }
            check(disposed == 2, "late final replies cannot reach reused connections: " + scenario);
        }
        finally { hold.TrySetResult(); }
    }

    private static async Task LiveProgress(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var bytes = Enumerable.Repeat((byte)33, 131072).ToArray();
        source.Files["payload.r00"] = bytes;
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.BeforeTransferFinalReply = _ => hold.Task.WaitAsync(TimeSpan.FromSeconds(10));
        long visible = 16384;
        target.Listing = _ => target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Key == "payload.r00" ? Interlocked.Read(ref visible) : f.Value.Length }).ToList();
        var engine = Engine(source, target);
        var job = engine.StartFxp(new TransferRequest { FromSite = "source", ToSite = "target", SourcePath = "/release", DestPath = "/release",
            Race = true, ViaApi = true, MeshSites = mesh ? new() { "source", "target" } : new() });
        FileTransfer? Active() => engine.Job(job.Id)!.Files.LastOrDefault(f => f.Name == "payload.r00" && f.Status == "active");
        try
        {
            await Until(() => Active()?.ObservedBytes == 16384);
            check(engine.Job(job.Id)!.CumulativeBytes == 0 && engine.SiteStats().Sum(s => s.InBytes + s.OutBytes) == 0,
                $"observed active FXP bytes do not credit a transfer before both final replies (mesh={mesh})");
            Interlocked.Exchange(ref visible, 65536);
            await Until(() => Active()?.ObservedBytes == 65536 && Active()!.Bps > 0);
            check(Active()!.ProgressObservedAt.HasValue && Active()!.Bps > 0, $"FXP progress and speed come from observed listing changes (mesh={mesh})");
            Interlocked.Exchange(ref visible, bytes.Length);
            hold.TrySetResult();
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && engine.Job(job.Id)!.CumulativeBytes == bytes.Length + source.Files["release.sfv"].Length,
                $"live progress never double counts completed bytes (mesh={mesh})");
        }
        finally { hold.TrySetResult(); if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id); await Until(() => engine.Job(job.Id)!.Terminal); }
    }

    private static async Task NestedPolling(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var roots = 0;
        var nested = 0;
        var dirs = new[] { "Sample", "Proof", "Subs" };
        source.Files["nested.r00"] = new byte[128];
        source.Listing = path =>
        {
            if (path == "/release")
            {
                Interlocked.Increment(ref roots);
                return dirs.Select(n => new RemoteEntry { Name = n, Type = "dir" }).Concat(source.Files.Where(f => f.Key != "nested.r00").Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length })).ToList();
            }
            Interlocked.Increment(ref nested);
            return path.EndsWith("/Sample") ? new() { new RemoteEntry { Name = "nested.r00", Type = "file", Size = 128 } } : new();
        };
        target.Listing = path => path == "/release"
            ? new[] { new RemoteEntry { Name = "Sample", Type = "dir" } }.Concat(target.Files.Where(f => f.Key != "nested.r00").Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length })).ToList()
            : path.EndsWith("/Sample") && target.Files.ContainsKey("nested.r00")
                ? new() { new RemoteEntry { Name = "nested.r00", Type = "file", Size = 128 } } : new();
        var engine = Engine(source, target);
        var job = engine.StartFxp(new TransferRequest { FromSite = "source", ToSite = "target", SourcePath = "/release", DestPath = "/release",
            Race = true, ViaApi = true, MeshSites = new() { "source", "target" } });
        try
        {
            await Until(() => Volatile.Read(ref roots) >= 20);
            var rootCount = Volatile.Read(ref roots);
            var nestedCount = Volatile.Read(ref nested);
            check(rootCount >= 20 && nestedCount < rootCount, $"mesh root polling remains fast while nested scans are bounded (root={rootCount}, nested={nestedCount})");
            target.Files.TryRemove("nested.r00", out _);
            source.Files["payload.r00"] = new byte[4096];
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\nSample/nested.r00 00000000\r\n");
            var watch = Stopwatch.StartNew();
            await Until(() => target.Files.ContainsKey("payload.r00"));
            check(watch.Elapsed < TimeSpan.FromSeconds(1), "new root payload starts promptly despite existing nested folders");
            await Until(() => engine.Job(job.Id)!.Terminal);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files.ContainsKey("nested.r00") && engine.Job(job.Id)!.Files.Count(f => f.Name == "Sample/nested.r00" && f.Status == "done") == 2, "mesh completion repairs a deleted nested file using a fresh tree");
        }
        finally { if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id); await Until(() => engine.Job(job.Id)!.Terminal); }
    }

    private static async Task Until(Func<bool> predicate)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!predicate()) { if (DateTime.UtcNow > end) throw new Exception("cbftp regression timed out"); await Task.Delay(20); }
    }
}
