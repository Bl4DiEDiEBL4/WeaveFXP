using System.Collections;
using System.Reflection;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

internal static class SkiplistCompatibilityTests
{
    private const string Contents = @"(?i)^(sample(s)?|proof(s)?|subs?|cover(s)?)\/.*";
    private const string Roots = @"(?i)^(sample(s)?|proof(s)?|subs?|cover(s)?|(cd|dvd|dis(k|c))\d+)$";

    public static async Task Run(Action<bool, string> check)
    {
        // Reference: cbftp r1310 src/skiplist.cpp:85-140. Rule order is outermost,
        // every complete path/suffix is tested, and file/dir scopes stay independent.
        var report = new List<SkiplistEntry> {
            new() { Pattern = Contents, Regex = true },
            new() { Pattern = Roots, Regex = true, File = true, Dir = false, Action = SkiplistAction.Allow }
        };
        report.AddRange(Enumerable.Range(3, 14).Select(i => new SkiplistEntry { Pattern = "^never-" + i + "$", Regex = true }));
        report.Add(new() { Pattern = ".*", Regex = true });
        report.Add(new() { Pattern = "*", Action = SkiplistAction.Allow });
        check(SkiplistRules.Evaluate(report, "COVER", false) is { Verdict: SkiplistVerdict.Allow, Index: 1 },
            "report's file-only ALLOW stops before rule 17 for a file");
        check(SkiplistRules.Evaluate(report, "COVER", true) is { Verdict: SkiplistVerdict.Deny, Index: 16 },
            "report's file-only ALLOW does not silently allow directories (cbftp scope)");
        report[1].Dir = true;
        foreach (var name in new[] { "COVER", "Sample", "/release/COVER", "/release/Sample" })
            check(SkiplistRules.Evaluate(report, name, true) is { Verdict: SkiplistVerdict.Allow, Index: 1 },
                "directory ALLOW stops before catch-all for " + name);
        check(SkiplistRules.Evaluate(report, "/release/Sample/clip.mkv", false) is { Verdict: SkiplistVerdict.Deny, Index: 0 },
            "report's path regex denies Sample contents before later rules");
        check(SkiplistRules.Blocks(report.Take(2).ToArray(), "Sample/clip.mkv"),
            "coverage filtering preserves the report's path-based content DENY");
        var ordered = new List<SkiplistEntry> {
            new() { Pattern = "Sample/*.mkv", Action = SkiplistAction.Allow },
            new() { Pattern = "*.mkv" }
        };
        check(SkiplistRules.Evaluate(ordered, "/release/Sample/clip.mkv", false) is { Verdict: SkiplistVerdict.Allow, Index: 0 },
            "earlier path ALLOW wins over later leaf DENY");
        check(!SkiplistRules.Blocks(ordered, "Sample/clip.mkv"),
            "coverage and transfer filtering agree on path ALLOW");
        check(SkiplistRules.Evaluate(new[] { ordered[0] }, "Sample/nested/clip.mkv", false).Verdict == SkiplistVerdict.NoMatch,
            "wildcard star cannot consume directory separators");
        check(!SkiplistRules.Matches(new() { Pattern = "COVER", Regex = true }, "COVER.backup") &&
            SkiplistRules.Matches(new() { Pattern = "COVER|COVER.backup", Regex = true }, "COVER.backup"),
            "regex matches complete suffixes with normal alternation backtracking");
        var unique = new[] { new SkiplistEntry { Pattern = "Sample/*.nfo", Action = SkiplistAction.Unique } };
        var memory = new SkiplistUniqueState();
        check(!SkiplistRules.UniqueBlocks(unique, "Sample/first.nfo", Array.Empty<string>(), memory, "dst") &&
            SkiplistRules.UniqueBlocks(unique, "Sample/second.nfo", Array.Empty<string>(), memory, "dst"),
            "path-based Unique rules reserve a destination slot");
        check(SkiplistRules.UniqueSatisfied(unique, "Sample/second.nfo", new[] { "Sample/first.nfo" }, memory, "dst") &&
            !SkiplistRules.UniqueSatisfied(unique, "Sample/second.nfo", Array.Empty<string>(), memory, "dst"),
            "path-based Unique coverage requires actual destination evidence");
        await Listing(check);
        await TypedRootFile(check);
    }

    private static async Task Listing(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        server.Listing = path => path.TrimEnd('/') switch {
            "/release" => new() { new() { Name = "Sample", Type = "dir" }, new() { Name = "COVER", Type = "dir" } },
            "/release/Sample" => new() { new() { Name = "clip.mkv", Type = "file", Size = 64 } },
            "/release/COVER" => new() { new() { Name = "image.jpg", Type = "file", Size = 64 } },
            _ => new()
        };
        var rules = new List<SkiplistEntry> {
            new() { Pattern = @"(?i)^sample\/.*", Regex = true },
            new() { Pattern = Roots, Regex = true, Action = SkiplistAction.Allow },
            new() { Pattern = "*.mkv", Action = SkiplistAction.Allow },
            new() { Pattern = "*.jpg", Action = SkiplistAction.Allow },
            new() { Pattern = ".*", Regex = true }
        };
        var engine = new WeaveEngine(Path.Combine(Path.GetTempPath(), "weave-skip-" + Guid.NewGuid().ToString("N"), "state.json"));
        using var client = await FtpClient.DialAndLoginAsync(FtpClient.Config.FromSite(server.Site("fixture", true, true)));
        var method = typeof(WeaveEngine).GetMethod("ListSourceFilesAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var task = (Task)method.Invoke(engine, new object?[] { client, "/release", rules, CancellationToken.None, true, null, null, true, true })!;
        await task;
        var files = (IEnumerable)task.GetType().GetProperty("Result")!.GetValue(task)!;
        var names = files.Cast<object>().Select(f => (string)f.GetType().GetProperty("Rel")!.GetValue(f)!).ToArray();
        check(names.SequenceEqual(new[] { "COVER/image.jpg" }),
            "FTP walk allows COVER, denies Sample contents by full path, and keeps the permitted image");
    }

    private static async Task TypedRootFile(Action<bool, string> check)
    {
        await using var source = new TransferServer();
        await using var target = new TransferServer();
        source.Files["COVER"] = new byte[] { 1, 2, 3 };
        source.Reply = command => command == "CWD /COVER" ? "550 not a directory" : null;
        var cfg = FtpClient.Config.FromSite(source.Site("source", true, false));
        cfg.Skiplist = new() {
            new() { Pattern = Roots, Regex = true, File = true, Dir = false, Action = SkiplistAction.Allow },
            new() { Pattern = ".*", Regex = true }
        };
        await FxpTransfer.TransferAsync(cfg, FtpClient.Config.FromSite(target.Site("target", false, true)),
            new() { FromSite = "source", ToSite = "target", SourcePath = "/COVER", DestPath = "/COVER" }, (_, _) => { });
        check(target.Files.TryGetValue("COVER", out var bytes) && bytes.SequenceEqual(source.Files["COVER"]),
            "manual FXP checks the actual file type, so a directory DENY cannot override a file ALLOW");
        var folder = Path.Combine(Path.GetTempPath(), "weave-skip-download-" + Guid.NewGuid().ToString("N"));
        var engine = new WeaveEngine(Path.Combine(folder, "state.json"));
        var settings = engine.Settings(false);
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        var site = source.Site("source", true, false);
        site.Skiplist = cfg.Skiplist.Select(rule => rule.Clone()).ToList();
        engine.AddSite(site);
        var output = Path.Combine(folder, "COVER.bin");
        var job = engine.StartDownload(new() { Site = "source", SourcePath = "/COVER", DestPath = output });
        var until = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < until && engine.Job(job.Id) is { Terminal: false }) await Task.Delay(25);
        check(engine.Job(job.Id) is { State: JobState.Succeeded, FilesDone: 1 } && File.Exists(output) &&
            (await File.ReadAllBytesAsync(output)).SequenceEqual(source.Files["COVER"]),
            "manual download also resolves File/Dir scope before applying ALLOW");
    }
}
