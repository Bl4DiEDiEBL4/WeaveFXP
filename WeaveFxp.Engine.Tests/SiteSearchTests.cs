using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class SiteSearchTests
{
    public static TransferServer Fixture()
    {
        var server = new TransferServer();
        server.Files["release.sfv"] = new byte[64];
        server.Files["notes.nfo"] = new byte[20];
        server.Listing = path => path switch
        {
            "/" => new() { Entry("parent", "dir"), Entry("skip", "dir"), Entry("cycle -> /", "link"), Entry("notes.nfo") },
            "/parent" => new() { Entry("release.sfv"), Entry("Movie.GERMAN.2026", "dir"), Entry("..", "dir"), Entry("bad/name.sfv") },
            "/parent/Movie.GERMAN.2026" => new() { Entry("payload.rar") },
            "/skip" => new() { Entry("ignored.sfv") },
            _ => new()
        };
        return server;
    }

    private static RemoteEntry Entry(string name, string type = "file") =>
        new() { Name = name, Type = type, Size = 64, Modified = DateTime.Today };

    public static async Task Run(Action<bool, string> check)
    {
        var jsonOptions = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var apiRequest = System.Text.Json.JsonSerializer.Deserialize<SiteSearchRequest>("""{"max_depth":0,"case_sensitive":true,"max_results":2,"min_bytes":10}""", jsonOptions)!;
        check(apiRequest.MaxDepth == 0 && apiRequest.CaseSensitive && apiRequest.MaxResults == 2 && apiRequest.MinBytes == 10,
            "search API binds documented snake_case fields");
        var queueRequest = System.Text.Json.JsonSerializer.Deserialize<SiteSearchQueueRequest>("""{"result_ids":[2],"destination_site":"TARGET","destination_path":"/OUT"}""", jsonOptions)!;
        check(queueRequest.ResultIds.SequenceEqual(new[] { 2 }) && queueRequest.DestinationSite == "TARGET" && queueRequest.DestinationPath == "/OUT",
            "search queue API binds selected results and FXP destination");
        var request = new SiteSearchRequest { Recursive = true, Include = "*.sfv;*.nfo", Exclude = "sample*" };
        var filter = new SiteSearchFilter(request);
        check(filter.Matches(Entry("Release.SFV")) && filter.Matches(Entry("notes.nfo")), "site search accepts semicolon wildcard alternatives ignoring case");
        check(!filter.Matches(Entry("sample.sfv")) && !filter.Matches(Entry("release.rar")), "site search applies include and exclude filters");
        check(!new SiteSearchFilter(request with { CaseSensitive = true }).Matches(Entry("Release.SFV")), "case-sensitive search is optional");
        check(new SiteSearchFilter(request with { Include = "[._-](GERMAN|FRENCH)[._-]", Regex = true }).Matches(Entry("Movie.GERMAN.2026")), "site search accepts the issue's regex example");
        check(!new SiteSearchFilter(request with { Include = "*", Kind = "dir" }).Matches(Entry("file")), "folder-only search excludes files");
        check(!new SiteSearchFilter(request with { Include = "*", MinBytes = 65 }).Matches(Entry("small")), "file size range is enforced");
        check(new SiteSearchFilter(request with { Include = "*", MinBytes = 65 }).Matches(Entry("folder", "dir")), "folder results are not filtered by directory listing size");
        check(new SiteSearchFilter(request with { Include = "*", DateFrom = DateTime.Today, DateTo = DateTime.Today }).Matches(Entry("today")), "date range endpoints are inclusive");
        check(!new SiteSearchFilter(request with { Include = "*", NotOlderThanDays = 2 }).Matches(new RemoteEntry { Name = "unknown", Type = "file" }), "unknown dates cannot satisfy date filters");
        try { _ = new SiteSearchFilter(request with { Regex = true, Include = "(" }); check(false, "invalid regex rejected"); }
        catch (ArgumentException) { check(true, "invalid regex rejected before starting a search"); }
        try { _ = new SiteSearchFilter(request with { MaxDepth = 1000 }); check(false, "unbounded depth rejected"); }
        catch (ArgumentException) { check(true, "unbounded depth rejected"); }
        check(SiteSearchFilter.NormalizePath("//path//sub/") == "/path/sub", "search roots are normalized");
        try { SiteSearchFilter.NormalizePath("/path/../other"); check(false, "relative path rejected"); }
        catch (ArgumentException) { check(true, "relative path components are rejected"); }
        check(!SiteSearchFilter.SafeName("../outside") && !SiteSearchFilter.SafeName("bad\r\nDELE x"), "unsafe listing names cannot enter search or queue");

        await using var server = Fixture();
        var root = Path.Combine(Path.GetTempPath(), "weave-search-tests-" + Guid.NewGuid().ToString("N"));
        var engine = new WeaveEngine(Path.Combine(root, "state.json"));
        engine.AddSite(server.Site("search-test", true, true));
        async Task<SiteSearchSnapshot> Search(SiteSearchRequest options)
        {
            var search = engine.StartSiteSearch(options with { Sites = new() { "search-test" } });
            for (var i = 0; i < 400 && search.Status == "running"; i++)
            {
                await Task.Delay(20);
                search = engine.SiteSearch(search.Id, 0, 10000)!;
            }
            return search;
        }
        var nativeCommands = new System.Collections.Concurrent.ConcurrentQueue<string>();
        server.BeforeCommand = command => { nativeCommands.Enqueue(command); return Task.CompletedTask; };
        server.Reply = command => command.StartsWith("SITE SEARCH ")
            ? "200- (Values displayed after dir names are Files/Megs/Age)\r\n200- Doing case-insensitive search for 'Movie':\r\n200- /archive/Movie Name.GERMAN (12F/100.0M/2d 03h)\r\n200- /other/Movie (1F/2.0M/unknown)\r\n200 2 directories found."
            : null;
        var nativeRequest = new SiteSearchRequest { Include = "Movie", Paths = new() { "/archive" } };
        check(!nativeRequest.Recursive, "native SITE SEARCH is the default for API requests");
        var native = await Search(nativeRequest);
        check(native.Status == "completed" && native.Results.Count == 1 && native.Results[0].Path == "/archive/Movie Name.GERMAN",
            "native search parses server paths with spaces and metadata and respects root filters");
        check(native.CommandsCompleted == 1 && native.CommandsPending == 0 && native.DirectoriesScanned == 0 &&
            nativeCommands.Count(command => command == "SITE SEARCH Movie") == 1 && !nativeCommands.Any(command => command.StartsWith("STAT") || command.StartsWith("LIST") || command.StartsWith("CWD")),
            "a native match finishes after one command with no recursive listings");
        check(native.Results[0].Modified == default && native.Results[0].Kind == "dir", "native results do not invent file metadata");
        nativeCommands.Clear();
        server.Reply = command => command.StartsWith("SITE SEARCH ") ? "500 SITE SEARCH not supported" : null;
        var unsupported = await Search(nativeRequest);
        check(unsupported.Status == "partial" && unsupported.Errors.Count == 1 && !nativeCommands.Any(command => command.StartsWith("STAT") || command.StartsWith("LIST")),
            "unsupported SITE SEARCH reports an error without silently crawling");
        server.Reply = command => command.StartsWith("SITE SEARCH ") ? "200 0 directories found." : null;
        check((await Search(nativeRequest)).TotalMatches == 0 && NativeSiteSearch.Parse("200 0 directories found.").Count == 0, "native no-match reply is understood");
        try { NativeSiteSearch.Parse("200-Doing search for '/archive/Movie':\r\n200 Done"); check(false, "unknown reply rejected"); }
        catch (IOException) { check(true, "a path in a server header cannot become a false search result"); }
        foreach (var invalid in new[] { "*", "Movie\r\nQUIT" })
        {
            try { engine.StartSiteSearch(nativeRequest with { Sites = new() { "search-test" }, Include = invalid }); check(false, "invalid native query rejected"); }
            catch (ArgumentException) { check(true, "broad or injected native query rejected before connecting"); }
        }
        var nativeBlocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeCommand = async command => { if (command.StartsWith("SITE SEARCH")) { nativeBlocked.TrySetResult(); await nativeRelease.Task; } };
        var nativeCancelled = engine.StartSiteSearch(nativeRequest with { Sites = new() { "search-test" } });
        try
        {
            await nativeBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.StopSiteSearch(nativeCancelled.Id);
            for (var i = 0; i < 100 && engine.SiteSearch(nativeCancelled.Id)!.Status == "running"; i++) await Task.Delay(20);
            var stopped = engine.SiteSearch(nativeCancelled.Id)!;
            check(stopped.Status == "cancelled" && stopped.CommandsPending == 0, "cancelling native search interrupts the command and clears pending work");
        }
        finally { nativeRelease.TrySetResult(); }
        server.Reply = null;
        server.BeforeCommand = null;
        var result = await Search(request with { Exclude = "skip", MaxDepth = 4 });
        check(result.Status == "completed" && result.Results.Select(x => x.Name).Order().SequenceEqual(new[] { "notes.nfo", "release.sfv" }),
            "FTP crawler searches unmatched parent folders, prunes excluded folders and skips symlinks");
        check(result.DirectoriesScanned == 3, "search avoids traversal through dot entries and symlinks");
        check(engine.SiteSearch(result.Id, 1, 1)!.Results.Count == 1 && engine.SiteSearch(result.Id, 1, 1)!.TotalMatches == 2, "search API snapshots paginate without losing total count");
        var shallow = await Search(request with { MaxDepth = 0 });
        check(shallow.Results.Count == 1 && shallow.Results[0].Name == "notes.nfo", "depth zero searches only the selected root");
        var limited = await Search(request with { MaxResults = 1 });
        check(limited.Status == "limited" && limited.TotalMatches == 1, "result limit is explicit rather than a false complete search");
        server.Reject = command => command.Contains("/skip") ? "550 denied" : null;
        var partial = await Search(request);
        check(partial.Status == "partial" && partial.Errors.Count > 0 && partial.TotalMatches == 2, "unreadable folders report partial results instead of silently disappearing");
        server.Reject = null;
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeCommand = async command => { if (command.StartsWith("STAT")) { blocked.TrySetResult(); await release.Task; } };
        var cancelled = engine.StartSiteSearch(request with { Sites = new() { "search-test" } });
        try
        {
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
            engine.StopSiteSearch(cancelled.Id);
            for (var i = 0; i < 100 && engine.SiteSearch(cancelled.Id)!.Status == "running"; i++) await Task.Delay(20);
            check(engine.SiteSearch(cancelled.Id)!.Status == "cancelled" && engine.SiteSearch(cancelled.Id)!.PendingDirectories == 0, "stop interrupts an in-progress FTP search listing and clears pending work");
        }
        finally { release.TrySetResult(); }
        server.BeforeCommand = null;
        check((await Search(request)).Status == "completed", "search releases its shared login slot after cancellation");
        try { engine.QueueSiteSearchResults(result.Id, new() { ResultIds = new() { 999 } }); check(false, "unknown result rejected"); }
        catch (ArgumentException) { check(true, "queue accepts only real results from that search"); }
    }
}
