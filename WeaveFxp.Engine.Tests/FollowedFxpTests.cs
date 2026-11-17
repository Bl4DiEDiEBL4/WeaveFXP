using System.Diagnostics;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

internal static class FollowedFxpTests
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var bidirectional in new[] { false, true }) await ConfirmedDupe(check, bidirectional);
        foreach (var mode in new[] { 0, 1, 2 })
        {
            foreach (var hint in new long[] { 1, 16384 }) await Accounting(check, mode, hint, "normal");
            await Accounting(check, mode, 65536, "unsupported");
            await Accounting(check, mode, 65536, "slow");
            // A smaller replacement is authoritative only with a single allowed source.
            if (mode != 2) await RepairAfterFollowing(check, mode, false);
            await RepairAfterFollowing(check, mode, true);
        }
        await MetadataOverTls(check);
        await CancelSize(check);
    }

    private static (WeaveEngine Engine, Job Job) Start(TransferServer source, TransferServer target, int mode)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-followed-tests-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 10000;
        settings.IncompleteRaceRetries = 0;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, mode == 2));
        engine.AddSite(target.Site("destination", mode == 2, true));
        var job = engine.StartFxp(new TransferRequest { Race = true, FromSite = "source", ToSite = "destination",
            SourcePath = "/source-release", DestPath = "/target-release", ViaApi = true,
            MeshSites = mode == 0 ? new() : new() { "source", "destination" } });
        return (engine, job);
    }

    private static async Task ConfirmedDupe(Action<bool, string> check, bool bidirectional)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var sourcePublished = 0;
        var targetPublished = 0;
        var bytes = Enumerable.Repeat((byte)37, 32).ToArray();
        source.Files["payload.r00"] = bytes;
        source.Listing = _ => source.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Key == "payload.r00" && Volatile.Read(ref sourcePublished) == 0 ? 1 : f.Value.Length }).ToList();
        target.Listing = _ => target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Key == "payload.r00" && Volatile.Read(ref targetPublished) == 0 ? 1 : f.Value.Length }).ToList();
        // Force the fallback path too: occupancy must be bounded even without SIZE.
        target.Reply = cmd => cmd == "SIZE payload.r00" ? "500 SIZE unsupported" : null;
        var (engine, job) = Start(source, target, bidirectional ? 2 : 1);
        try
        {
            await Until(() => engine.Job(job.Id)!.Files.Any(f => f.Name == "payload.r00" && f.Status == "done"), engine, job);
            await Task.Delay(150);
            target.Reject = cmd => cmd == "PRET STOR payload.r00" ? "553 payload.r00: File exists." : null;
            Volatile.Write(ref sourcePublished, 1);
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
            await Until(() => target.Rejections > 0, engine, job);
            await Task.Delay(1200);
            check(target.Rejections == 1, $"confirmed-file dupe is suppressed instead of retried thousands of times (bidirectional={bidirectional})");
            check(engine.Job(job.Id)!.Files.Count(f => f.Name == "payload.r00") == 2, $"one successful and one dupe row remain bounded (bidirectional={bidirectional})");
            check(!engine.Job(job.Id)!.Terminal, $"stale destination listing does not falsely finish the race (bidirectional={bidirectional})");
            Volatile.Write(ref targetPublished, 1);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].SequenceEqual(bytes), $"fresh matching listings finish without retransmitting the dupe (bidirectional={bidirectional})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task Accounting(Action<bool, string> check, int mode, long hint, string metadata)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var published = 0;
        var uploaded = 0;
        var sizeCalls = 0;
        var payload = Enumerable.Range(0, 65536).Select(n => (byte)(n * 17)).ToArray();
        source.Files["payload.r00"] = payload;
        source.Listing = _ => source.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Key == "payload.r00" && Volatile.Read(ref published) == 0 ? hint : f.Value.Length }).ToList();
        target.BeforeCommand = async cmd =>
        {
            if (cmd == "STOR payload.r00") Interlocked.Increment(ref uploaded);
            if (cmd == "SIZE payload.r00")
            {
                Interlocked.Increment(ref sizeCalls);
                if (metadata == "slow") await Task.Delay(800);
            }
        };
        if (metadata == "unsupported") target.Reply = cmd => cmd.StartsWith("SIZE ") ? "500 SIZE unsupported" : null;
        var (engine, job) = Start(source, target, mode);
        var label = $"mode={mode}, listed={hint}, metadata={metadata}";
        try
        {
            var watch = Stopwatch.StartNew();
            await Until(() => engine.Job(job.Id)!.CumulativeBytes >= payload.Length, engine, job);
            check(watch.Elapsed < TimeSpan.FromSeconds(4), "SIZE failure does not stall the successful transfer: " + label);
            var row = engine.Job(job.Id)!.Files.Single(f => f.Name == "payload.r00" && f.Status == "done");
            check(row.Size == payload.Length && row.Seconds > 0 && Math.Abs(row.Bps * row.Seconds - payload.Length) < .01,
                "completed row bytes and speed use final file size: " + label);
            check(metadata == "normal" ? row.Error == "" : row.Error.Contains("estimated"), "unverified accounting is explicitly identified: " + label);
            await Until(() => engine.SiteStats().Sum(s => s.InBytes) == payload.Length, engine, job);
            check(engine.SiteStats().Sum(s => s.OutBytes) == payload.Length && engine.Job(job.Id)!.CumulativeBytes == payload.Length,
                "site traffic and race bytes agree, without double counting: " + label);
            check(target.Files["payload.r00"].SequenceEqual(payload) && uploaded == 1 && sizeCalls == 1,
                "real FXP data is intact and measured once: " + label);
            await Task.Delay(300); // Source LIST still contains the old growing hint.
            check(uploaded == 1, "stale source hint does not requeue a verified completed file: " + label);
            check(engine.Job(job.Id)!.BytesTotal >= payload.Length, "release byte total incorporates verified following size: " + label);
            Volatile.Write(ref published, 1);
            source.Files["next.r01"] = new byte[128];
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\nnext.r01 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job);
            var completed = engine.Job(job.Id)!;
            var total = payload.Length + 128 + source.Files["release.sfv"].Length;
            check(completed.State == JobState.Succeeded && target.Files["next.r01"].Length == 128,
                "next transfer succeeds with clean control replies after metadata lookup: " + label);
            check(completed.CumulativeBytes == total && completed.Files.Where(f => f.Status == "done").Sum(f => f.Size) == total && uploaded == 1,
                "final totals count each completed file exactly once: " + label);
        }
        finally { await Stop(engine, job); }
    }

    private static async Task RepairAfterFollowing(Action<bool, string> check, int mode, bool delete)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var published = 0;
        source.Files["payload.r00"] = new byte[4096];
        source.Listing = _ => source.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Key == "payload.r00" && Volatile.Read(ref published) == 0 ? 1 : f.Value.Length }).ToList();
        var (engine, job) = Start(source, target, mode);
        try
        {
            await Until(() => engine.Job(job.Id)!.CumulativeBytes == 4096, engine, job);
            await Task.Delay(150);
            if (delete) target.Files.TryRemove("payload.r00", out _);
            var repaired = Enumerable.Repeat((byte)7, 2048).ToArray();
            source.Files["payload.r00"] = repaired;
            Volatile.Write(ref published, 1);
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files["payload.r00"].SequenceEqual(repaired),
                $"following-size hint does not hide a new smaller source or deleted target (mode={mode}, delete={delete})");
            check(engine.Job(job.Id)!.Files.Count(f => f.Name == "payload.r00" && f.Status == "done") == 2,
                $"following file repaired exactly once after source correction (mode={mode}, delete={delete})");
        }
        finally { await Stop(engine, job); }
    }

    public static async Task MetadataOverTls(Action<bool, string> check)
    {
        await using var server = new TransferServer(tls: true);
        using var connection = await FtpClient.DialAndLoginAsync(FtpClient.Config.FromSite(server.Site("tls-size", true, true)));
        foreach (var length in new[] { 0, 128, 65536 })
        {
            var bytes = Enumerable.Range(0, length).Select(n => (byte)n).ToArray();
            using var data = new MemoryStream(bytes);
            await connection.StoreFromAsync("payload.r00", data).WaitAsync(TimeSpan.FromSeconds(4));
            var result = await WeaveEngine.CompletedFxpSizeAsync(connection, "payload.r00", 1, CancellationToken.None);
            check(result == ((long)length, true, true) && server.Files["payload.r00"].SequenceEqual(bytes),
                $"FTPS measures final data size after 226, including empty files ({length} bytes)");
            var (code, _) = await connection.CommandAsync("NOOP");
            check(code == 200, $"FTPS SIZE leaves the next control command synchronized ({length} bytes)");
        }
    }

    private static async Task CancelSize(Action<bool, string> check)
    {
        await using var target = new TransferServer();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.BeforeCommand = async cmd => { if (cmd.StartsWith("SIZE ")) { entered.TrySetResult(); await Task.Delay(600); } };
        using var connection = await FtpClient.DialAndLoginAsync(new FtpClient.Config { Host = "127.0.0.1", Port = target.Site("target", false, true).Port, TimeoutSeconds = 3 });
        using var cancellation = new CancellationTokenSource();
        var probe = WeaveEngine.CompletedFxpSizeAsync(connection, "payload.r00", 17, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        var result = await probe.WaitAsync(TimeSpan.FromSeconds(1));
        check(result == (17L, false, false), "cancelling metadata lookup promptly discards its control connection");
        var unusable = false;
        try { await connection.CommandAsync("NOOP"); } catch { unusable = true; }
        check(unusable, "cancelled SIZE connection cannot leak its late 213 into a later command");
    }

    private static async Task Stop(WeaveEngine engine, Job job)
    {
        if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
        await Until(() => engine.Job(job.Id)!.Terminal, engine, job);
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, Job job)
    {
        var end = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > end) throw new Exception("Followed FXP timeout: " + string.Join("\n", engine.Job(job.Id)!.Events.TakeLast(12).Select(e => e.Message)));
            await Task.Delay(20);
        }
    }
}
