using System.Diagnostics;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class RaceStartupTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var allSingleBytesAllowed = true;
        for (uint value = 0; value < 256; value++)
        {
            uint crc = 0xffffffffu ^ value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xedb88320u;
            allSingleBytesAllowed &= !WeaveEngine.SfvExcludesSingleByte((~crc).ToString("X8"));
        }
        check(allSingleBytesAllowed, "SFV size guard preserves all 256 real one-byte CRCs");
        check(WeaveEngine.SfvExcludesSingleByte("C71C0011"), "SFV CRC of 4096 zero bytes excludes a one-byte completion hint");
        check(new[] { "", "garbage!", "1234567", "123456789" }.All(crc => !WeaveEngine.SfvExcludesSingleByte(crc)),
            "malformed SFV checksums do not create invented size evidence");
        await CompareStartup(check);
        foreach (var response in new[] { "550 Permission denied", "550 unavailable", "451 temporary failure" })
            await FailedListing(check, response);
        await PersistentListingFailure(check);
        await ListingRecovery(check);
        foreach (var mesh in new[] { false, true })
        {
            await PendingHints(check, false, mesh);
            await PendingHints(check, true, mesh);
        }
    }

    public static async Task CompareStartup(Action<bool, string> check)
    {
        foreach (var mesh in new[] { false, true }) await MissingTarget(check, mesh);
    }

    private static WeaveEngine Engine(TransferServer source, TransferServer target)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-startup-" + Guid.NewGuid().ToString("N"), "state.json"));
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 10000;
        settings.IncompleteRaceRetries = 0;
        settings.RaceDestinationPrecheck = true;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        return engine;
    }

    private static Job Start(WeaveEngine engine, bool mesh) => engine.StartFxp(new TransferRequest
    {
        Race = true, ViaApi = true, FromSite = "source", ToSite = "target",
        SourcePath = "/release", DestPath = "/release",
        MeshSites = mesh ? new() { "source", "target" } : new()
    });

    private static async Task MissingTarget(Action<bool, string> check, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        var exists = 0;
        var made = 0;
        var sourceWrites = 0;
        target.Reply = command =>
        {
            if (command == "CWD /release" && Volatile.Read(ref exists) == 0)
                return "550 /release: No such file or directory";
            if (command == "MKD release")
            {
                Interlocked.Increment(ref made);
                Volatile.Write(ref exists, 1);
                return "257 directory created";
            }
            return null;
        };
        source.BeforeCommand = command =>
        {
            if (command.StartsWith("MKD ") || command.StartsWith("STOR ")) Interlocked.Increment(ref sourceWrites);
            return Task.CompletedTask;
        };
        var engine = Engine(source, target);
        var timer = Stopwatch.StartNew();
        var job = Start(engine, mesh);
        try
        {
            await Wait(() => engine.Job(job.Id)!.Terminal, 3000);
            check(engine.Job(job.Id)!.State == JobState.Succeeded,
                $"race creates missing target without waiting for another uploader (mesh={mesh})");
            check(made == 1 && target.Files["payload.r00"].Length == 4096 && sourceWrites == 0,
                $"first worker creates target once; blocked reverse route stays untouched (mesh={mesh})");
            Console.WriteLine($"MEASURE: missing-target race finished in {timer.ElapsedMilliseconds} ms (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task FailedListing(Action<bool, string> check, string response)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        var writes = 0;
        var failures = 0;
        target.Reply = command =>
        {
            if (command == "CWD /release") { Interlocked.Increment(ref failures); return response; }
            return null;
        };
        target.BeforeCommand = command =>
        {
            if (command.StartsWith("MKD ") || command.StartsWith("STOR ")) Interlocked.Increment(ref writes);
            return Task.CompletedTask;
        };
        var engine = Engine(source, target);
        var job = Start(engine, true);
        try
        {
            await Wait(() => Volatile.Read(ref failures) >= 3, 3000);
            check(failures >= 3 && writes == 0 && engine.Job(job.Id)!.State != JobState.Succeeded,
                "listing failure is not absence or permission to upload: " + response);
        }
        finally { await Stop(engine, job); }
    }

    private static async Task PendingHints(Action<bool, string> check, bool realSingleByte, bool mesh)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var published = 0;
        var payload = new byte[realSingleByte ? 1 : 4096];
        var crc = realSingleByte ? "D202EF8D" : "C71C0011"; // CRC32 of 1 / 4096 zero bytes.
        var sfv = Encoding.ASCII.GetBytes($"payload.r00 {crc}\r\n");
        foreach (var server in new[] { source, target })
        {
            server.Files["payload.r00"] = payload;
            server.Files["release.sfv"] = sfv;
            server.Listing = _ => server.Files.Select(f => new RemoteEntry
            {
                Name = f.Key, Type = "file",
                Size = f.Key == "payload.r00" && Volatile.Read(ref published) == 0 ? 1 : f.Value.Length
            }).ToList();
        }
        var engine = Engine(source, target);
        var job = Start(engine, mesh);
        try
        {
            await Wait(() => engine.Job(job.Id)!.Events.Any(e => e.Message.StartsWith("parsed release.sfv")), 3000);
            await Task.Delay(600);
            if (realSingleByte)
            {
                check(engine.Job(job.Id)!.State == JobState.Succeeded,
                    $"genuine one-byte SFV file can still complete without an arbitrary size cutoff (mesh={mesh})");
                return;
            }
            check(!engine.Job(job.Id)!.Terminal,
                $"equal one-byte upload hints cannot finish an SFV race before real files arrive (mesh={mesh})");
            Volatile.Write(ref published, 1);
            source.Files["next.r01"] = new byte[128];
            source.Files["release.sfv"] = Encoding.ASCII.GetBytes($"payload.r00 {crc}\r\nnext.r01 C2A8FA9D\r\n");
            await Wait(() => engine.Job(job.Id)!.Terminal, 4000);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && target.Files.ContainsKey("next.r01"),
                $"race stays alive to upload later files after pending one-byte listings grow (mesh={mesh})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task PersistentListingFailure(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        var failures = 0;
        target.Reply = command =>
        {
            if (command == "CWD /release") { Interlocked.Increment(ref failures); return "550 Permission denied"; }
            return null;
        };
        var engine = Engine(source, target);
        var settings = engine.Settings(false);
        settings.RaceMaxIdleCycles = 61;
        engine.UpdateSettings(settings);
        var job = Start(engine, true);
        try
        {
            await Wait(() => engine.Job(job.Id)!.Terminal, 10000);
            check(engine.Job(job.Id)!.State == JobState.Failed,
                "unusable destination listing cannot keep an idle race alive forever");
            check(failures <= 12 && target.Files.IsEmpty,
                $"persistent listing failures back off without blind uploads (requests={failures})");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task ListingRecovery(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["payload.r00"] = new byte[4096];
        source.Files["release.sfv"] = Encoding.ASCII.GetBytes("payload.r00 C71C0011\r\n");
        var failures = 0;
        target.Reply = command => command == "CWD /release" && Interlocked.Increment(ref failures) <= 3
            ? "451 temporary listing failure" : null;
        var engine = Engine(source, target);
        var job = Start(engine, true);
        try
        {
            await Wait(() => engine.Job(job.Id)!.Terminal, 5000);
            check(engine.Job(job.Id)!.State == JobState.Succeeded && failures >= 4 && target.Files.ContainsKey("payload.r00"),
                "fresh listing after backoff restores race candidates without a new announce");
        }
        finally { await Stop(engine, job); }
    }

    private static async Task Wait(Func<bool> condition, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
    }

    private static async Task Stop(WeaveEngine engine, Job job)
    {
        if (!engine.Job(job.Id)!.Terminal) engine.CancelJob(job.Id);
        await Wait(() => engine.Job(job.Id)!.Terminal, 4000);
    }
}
