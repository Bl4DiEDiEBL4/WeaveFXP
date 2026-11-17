using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class ClaudeRegressionTests
{
    public static async Task Run(Action<bool, string> check)
    {
        Skiplists(check);
        await GlobalStop(check);
        await DestructivePaths(check);
        await ResetHistory(check);
        await RaceTopology(check);
        await RemovedSiteCompletion(check);
    }

    private static void Skiplists(Action<bool, string> check)
    {
        var legacy = JsonSerializer.Deserialize<Site>("""{"Skiplist":["Sample","*.txt"]}""")!.WithDefaults();
        check(legacy.Skiplist.Count == 2 && SkiplistRules.Blocks(legacy.Skiplist, "Sample/clip.mkv") &&
            SkiplistRules.Blocks(legacy.Skiplist, "notes.txt"), "legacy string skiplists retain file and directory exclusions");
        var rules = new List<SkiplistEntry> { new() { Pattern = "keep.nfo", Action = SkiplistAction.Allow },
            new() { Pattern = @".*\.nfo$", Regex = true, Dir = false } };
        check(!SkiplistRules.Blocks(rules, "keep.nfo") && SkiplistRules.Blocks(rules, "other.nfo") &&
            !SkiplistRules.Blocks(rules, "folder.nfo/payload.rar"), "ordered allow/deny and file scope remain distinct");
        check(!SkiplistRules.Matches(new() { Pattern = "[", Regex = true }, "file") &&
            SkiplistRules.ValidationError(new() { Pattern = "[", Regex = true }) is not null, "invalid regex is reported without crashing evaluation");
        rules = new() { new() { Pattern = "*.nfo", Action = SkiplistAction.Unique, Dir = false } };
        var state = new SkiplistUniqueState();
        check(!SkiplistRules.UniqueBlocks(rules, "first.nfo", Array.Empty<string>(), state, "target") &&
            SkiplistRules.UniqueBlocks(rules, "second.nfo", Array.Empty<string>(), state, "target"), "unique file reservations reject a second concurrent candidate");
        check(!SkiplistRules.UniqueSatisfied(rules, "second.nfo", Array.Empty<string>(), state, "target"),
            "an untransferred unique reservation is not verified destination coverage");
        check(SkiplistRules.UniqueSatisfied(rules, "second.nfo", new[] { "first.nfo" }, state, "target"),
            "an existing unique file can satisfy an alternative on that destination");
        rules = new() { new() { Pattern = "Disc*", Action = SkiplistAction.Unique, File = false, Dir = true } };
        state = new();
        check(!SkiplistRules.UniqueBlocks(rules, "Disc1/first.rar", Array.Empty<string>(), state, "target") &&
            SkiplistRules.UniqueBlocks(rules, "Disc2/second.rar", Array.Empty<string>(), state, "target"),
            "unique directory rules reserve one directory per parent");
        check(SkiplistRules.UniqueSatisfied(rules, "Disc2/second.rar", new[] { "Disc1/first.rar" }, state, "target"),
            "unique directory coverage considers existing descendants");
        check(!SkiplistRules.UniqueBlocks(rules, "Disc1/another.rar", new[] { "Disc1/first.rar" }, state, "target") &&
            !SkiplistRules.UniqueBlocks(rules, "Disc2/second.rar", Array.Empty<string>(), state, "another-target"),
            "unique directory reservations retain sibling files and independent destinations");
    }

    private static WeaveEngine Engine(out JsonStore store)
    {
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-claude-" + Guid.NewGuid().ToString("N"), "state.json"));
        store = (JsonStore)typeof(WeaveEngine).GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        return engine;
    }

    private static Job Seed(JsonStore store, string id, JobState state = JobState.Failed, string root = "/release", string file = "payload.rar") =>
        store.UpsertJob(new Job { Id = id, Type = JobType.Race, State = state, CreatedAt = DateTime.UtcNow,
            Request = new() { Race = true, FromSite = "source", ToSite = "target", SourcePath = root, DestPath = root,
                DryRun = true, MeshSites = new() { "source", "target" }, SitePaths = new(StringComparer.OrdinalIgnoreCase) { ["source"] = root, ["target"] = root } },
            Files = new() { new() { Name = file, Status = "done", Size = 64, FromSite = "source", ToSite = "target" } },
            SfvFiles = new() { new() { Name = file, Crc = "00000000", Size = 64, UploadedByUs = true, UploadedSize = 64 } } });

    private static async Task GlobalStop(Action<bool, string> check)
    {
        var engine = Engine(out var store);
        engine.AddSite(new() { Name = "source", Host = "127.0.0.1", Port = 1, AllowDownload = true });
        engine.AddSite(new() { Name = "target", Host = "127.0.0.1", Port = 1, AllowUpload = true });
        Seed(store, "retry");
        engine.PauseRacing();
        check(engine.RetryJob("retry"), "retry can be queued while global racing is stopped");
        await Task.Delay(350);
        check(engine.Job("retry") is { Terminal: false, Paused: true }, "global stop keeps retried jobs held without starting a run");
        check(!engine.ResumeJob("retry") && engine.Job("retry")!.Paused, "per-job Resume cannot bypass global stop");
        engine.CancelJob("retry");
        Seed(store, "manual", JobState.Queued);
        engine.PauseJob("manual");
        engine.ResumeRacing();
        check(engine.Job("manual")!.Paused, "global Start does not resume jobs that were manually paused");
        engine.CancelJob("manual");
        Seed(store, "held", JobState.Queued);
        engine.PauseRacing();
        engine.ResumeRacing();
        check(!engine.Job("held")!.Paused, "global Start resumes jobs actually held by global Stop");
        engine.CancelJob("held");
    }

    private static async Task DestructivePaths(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        var commands = new ConcurrentQueue<string>();
        target.BeforeCommand = cmd => { commands.Enqueue(cmd); return Task.CompletedTask; };
        var engine = Engine(out var store);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        bool HasMutation() => commands.Any(c => c.StartsWith("DELE ") || c.StartsWith("RMD ") || c.StartsWith("SITE NUKE "));
        foreach (var invalid in new[] { "../outside.rar", "sub/../../outside.rar", "/outside.rar", "file\r\nDELE outside.rar" })
        {
            commands.Clear();
            Seed(store, "bad-file", file: invalid);
            var result = await engine.DeleteRaceFilesAsync("bad-file", "target", RaceFileScope.Own, false);
            check(!result.Ok && !HasMutation(), $"own-file cleanup refuses escaping/injected history path {JsonSerializer.Serialize(invalid)}");
        }
        commands.Clear();
        Seed(store, "bad-root", root: "/release/..");
        var badRoot = await engine.DeleteRaceFilesAsync("bad-root", "target", RaceFileScope.All, false);
        check(!badRoot.Ok && !HasMutation(), "delete-all refuses a traversal release root before issuing destructive commands");
        commands.Clear();
        Seed(store, "running", JobState.Running);
        var running = await engine.DeleteRaceFilesAsync("running", "target", RaceFileScope.Own, false);
        check(!running.Ok && !HasMutation(), "cleanup refuses a running site that has not been removed and drained");
        engine.CancelJob("running");
        commands.Clear();
        var foreign = Seed(store, "foreign");
        store.UpdateJob(foreign.Id, j => j.Request.SitePaths["foreign"] = "/unrelated");
        engine.AddSite(target.Site("foreign", false, true));
        var outside = await engine.DeleteRaceFilesAsync(foreign.Id, "foreign", RaceFileScope.All, false);
        check(!outside.Ok && !HasMutation(), "race cleanup refuses unrelated sites even if a stray SitePaths entry exists");
        foreach (var path in new[] { "/release/..", "/release/.", "/release/name\r\nDELE outside" })
        {
            commands.Clear();
            var nuke = await engine.NukeAsync("target", new[] { path }, 1, "duplicate");
            check(nuke.All(r => !r.Ok) && !HasMutation(), $"NUKE refuses unsafe selected directory {JsonSerializer.Serialize(path)}");
        }
        var drain = typeof(WeaveEngine).GetMethod("WaitForSiteTransfersAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Seed(store, "drain", JobState.Running);
        store.UpdateJob("drain", j => j.Files[0].Status = "active");
        var wait = (Task)drain.Invoke(engine, new object[] { "drain", "target", TimeSpan.FromMilliseconds(20), CancellationToken.None })!;
        await wait;
        check(wait.GetType().GetProperty("Result")?.GetValue(wait) is false, "transfer drain timeout is reported as failure rather than permitting deletion");
        engine.CancelJob("drain");

        foreach (var malicious in new[] { "../outside.rar", "remote-link" })
        {
            commands.Clear();
            Seed(store, "tree");
            target.Listing = p => p == "/release"
                ? new() { new() { Name = malicious, Type = malicious == "remote-link" ? "link" : "file", Size = 1 } }
                : new() { new() { Name = "outside.rar", Type = "file", Size = 1 } };
            target.Reply = cmd => cmd is "DELE /release" or "DELE /release/remote-link" ? "550 directory" : null;
            var tree = await engine.DeleteRaceFilesAsync("tree", "target", RaceFileScope.All, false);
            check(!tree.Ok && !commands.Any(c => c.Contains("/release/../outside") || c.Contains("/remote-link/outside") ||
                    (c.StartsWith("STAT ") && c.Contains("remote-link"))),
                $"delete-all never follows an escaping or linked child: {malicious}");
        }
        target.Listing = null;
        target.Reply = null;
        commands.Clear();
        Seed(store, "parent", file: "sub/payload.rar");
        target.Reject = cmd => cmd == "CWD /" ? "550 permission denied" : null;
        var parent = await engine.DeleteRaceFilesAsync("parent", "target", RaceFileScope.Own, false);
        check(!parent.Ok && !commands.Contains("RMD release"), "failed parent CWD cannot issue RMD in the previous directory");
        target.Reject = null;

        Seed(store, "serialized");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        target.BeforeCommand = async cmd =>
        {
            if (cmd == "DELE payload.rar") { entered.TrySetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        };
        var deletion = engine.DeleteRaceFilesAsync("serialized", "target", RaceFileScope.Own, false);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            check(!engine.RetryJob("serialized") && !(await engine.AbortRaceAsync("serialized", RaceFileScope.None, false)).Ok,
                "retry and another race action cannot overlap remote cleanup");
        }
        finally { release.TrySetResult(); await deletion; }
    }

    private static async Task ResetHistory(Action<bool, string> check)
    {
        var engine = Engine(out var store);
        engine.AddSite(new() { Name = "source", Host = "127.0.0.1", Port = 1, AllowDownload = true });
        engine.AddSite(new() { Name = "target", Host = "127.0.0.1", Port = 1, AllowUpload = true });
        Seed(store, "reset");
        check(engine.ResetRace("reset", hard: true).Ok, "hard reset restarts a terminal dry-run race");
        await Task.Delay(200);
        check(engine.Job("reset")!.Files.Count == 0 && engine.Job("reset")!.SfvFiles.Count == 0,
            "hard reset clears SFV upload ownership as well as file attempts");
        if (!engine.Job("reset")!.Terminal) engine.CancelJob("reset");
    }

    private static async Task RaceTopology(Action<bool, string> check)
    {
        var engine = Engine(out var store);
        foreach (var name in new[] { "source", "target", "third" })
            engine.AddSite(new() { Name = name, Host = "127.0.0.1", Port = 1, AllowDownload = true, AllowUpload = true });
        Seed(store, "topology", JobState.Running);
        store.UpdateJob("topology", j =>
        {
            j.Request.MeshSites.Add("third");
            j.Request.SitePaths["third"] = "/release";
            j.Request.ViaApi = true;
            j.Paused = true;
        });
        check((await engine.RemoveSiteFromRaceAsync("topology", "third", RaceFileScope.None)).Ok &&
            !engine.RaceSiteNames("topology").Contains("third"), "removing a site takes it out of the current topology");
        var added = engine.AddSiteToRace("topology", "third", "/release");
        check(added.Ok && added.NewJobId == "topology" && engine.RaceSiteNames("topology").Contains("third") && engine.Job("topology")!.Paused,
            "re-adding a removed API site restores the existing paused race instead of coalescing a no-op");
        engine.CancelJob("topology");

        var pre = engine.Site("target")!;
        pre.Affils.Add("GROUP"); pre.PreTargetSites.Add("third"); pre.PreRouteMode = PreRouteMode.First;
        engine.SaveSite("target", pre);
        engine.PauseRacing();
        var routed = engine.StartSpread(new() { Race = true, DryRun = true, FromSite = "source", ToSites = new() { "target", "third" },
            SourcePath = "/incoming/Release-GROUP", DestPath = "/archive/Release-GROUP", Label = "Release-GROUP" }).Jobs.Single();
        check(routed.Request.FromSite == "target" && routed.Request.SourcePath == "/archive/Release-GROUP" &&
            routed.Request.SitePaths["source"] == "/incoming/Release-GROUP", "pre-source rewriting preserves every site's original release path");
        engine.CancelJob(routed.Id);
        pre.PreRouteMode = PreRouteMode.Only;
        engine.SaveSite("target", pre);
        Job? mesh = null;
        try
        {
            mesh = engine.StartFxp(new() { Race = true, DryRun = true, FromSite = "target", ToSite = "mesh",
                MeshSites = new() { "target", "third" }, SourcePath = "/archive/Release-GROUP", DestPath = "/archive/Release-GROUP",
                Label = "Release-GROUP" });
        }
        catch (IOException) { }
        check(mesh is not null, "direct mesh API validates real pre targets rather than rejecting the synthetic mesh name");
        if (mesh is not null) engine.CancelJob(mesh.Id);
    }

    private static async Task RemovedSiteCompletion(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        foreach (var server in new[] { source, target })
        {
            server.Files["payload.rar"] = new byte[64];
            server.Files["release.sfv"] = System.Text.Encoding.ASCII.GetBytes("payload.rar 00000000\r\n");
        }
        target.Listing = _ => target.Files.Select(f => new RemoteEntry { Name = f.Key, Type = "file", Size = f.Value.Length })
            .Append(new RemoteEntry { Name = "[WFTP] - ( COMPLETE ) - [WFTP]", Type = "dir" }).ToList();
        var engine = Engine(out var store);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(target.Site("target", false, true));
        engine.AddSite(new() { Name = "removed", Host = "127.0.0.1", Port = 1, AllowUpload = true, TimeoutSeconds = 1 });
        Seed(store, "completion", JobState.Running);
        store.UpdateJob("completion", j => { j.Request.DryRun = false; j.Request.MeshSites.Add("removed"); });
        await engine.RemoveSiteFromRaceAsync("completion", "removed", RaceFileScope.None);
        store.UpdateJob("completion", j => j.State = JobState.Failed);
        check(await engine.RecheckRaceAsync("completion") && engine.Job("completion")!.State == JobState.Succeeded,
            "completion reconciliation excludes sites deliberately removed from the race");
    }
}
