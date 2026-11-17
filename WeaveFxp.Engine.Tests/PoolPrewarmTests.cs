using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class PoolPrewarmTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await SmallFleet(check);
        await LargeFleet(check);
        await SharedBudget(check);
    }

    private static Site Site(TransferServer server, int index)
    {
        var site = server.Site($"site-{index:D2}", true, true);
        site.Username = site.Name;
        site.TimeoutSeconds = 15;
        return site;
    }

    private static WeaveEngine Engine(IEnumerable<Site> sites)
    {
        var path = Path.Combine(Path.GetTempPath(), "weave-prewarm-tests-" + Guid.NewGuid().ToString("N"), "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var state = new State { Sites = sites.ToDictionary(site => site.Name) };
        File.WriteAllText(path, JsonSerializer.Serialize(state,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        return new WeaveEngine(path);
    }

    // Await the same private entry points used by the fire-and-forget UI handlers.
    private static Task Prime(WeaveEngine engine, Site? site = null) =>
        (Task)typeof(WeaveEngine).GetMethod(site is null ? "PrimeSitePoolsAsync" : "PrimeSitePoolAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, site is null ? null : new object[] { site })!;

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("Prewarm test timed out");
            await Task.Delay(20);
        }
    }

    private static async Task SmallFleet(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        var logins = new ConcurrentDictionary<string, int>();
        server.BeforeCommand = command =>
        {
            if (command.StartsWith("USER ")) logins.AddOrUpdate(command[5..], 1, (_, count) => count + 1);
            return Task.CompletedTask;
        };
        var sites = Enumerable.Range(1, 16).Select(i => Site(server, i)).ToArray();
        sites[0].LoginSlots = 1;
        sites[1].LoginSlots = 2;
        var engine = Engine(sites);
        await Prime(engine).WaitAsync(TimeSpan.FromSeconds(10));
        check(!logins.ContainsKey(sites[0].Name), "prewarm leaves a single-login site's only control slot free");
        check(logins.GetValueOrDefault(sites[1].Name) == 1, "prewarm respects the two-login site's reserved control slot");
        check(sites.Skip(2).All(site => logins.GetValueOrDefault(site.Name) == 2),
            "fleet of exactly sixteen sites warms at most two sessions per site");
        await Task.WhenAll(Prime(engine), Prime(engine), Prime(engine, sites[2])).WaitAsync(TimeSpan.FromSeconds(10));
        check(logins.Values.Sum() == 29, "overlapping prewarms reuse physical sessions instead of adding logins");
    }

    private static async Task LargeFleet(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        var logins = new ConcurrentDictionary<string, int>();
        server.BeforeCommand = command =>
        {
            if (command.StartsWith("USER ")) logins.AddOrUpdate(command[5..], 1, (_, count) => count + 1);
            return Task.CompletedTask;
        };
        server.Reply = command => command.StartsWith("SITE SEARCH ") ? "200 0 directories found." : null;
        var sites = Enumerable.Range(1, 17).Select(i => Site(server, i)).ToArray();
        var engine = Engine(sites);
        await Prime(engine).WaitAsync(TimeSpan.FromSeconds(10));
        check(logins.IsEmpty, "fleet of seventeen sites skips startup prewarm");
        var added = engine.AddSite(Site(server, 18));
        var saved = engine.SaveSite(sites[0].Name, sites[0]);
        engine.UpdateSettings(engine.Settings(false));
        await Task.WhenAll(Prime(engine), Prime(engine, added), Prime(engine, saved)).WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(100);
        check(logins.IsEmpty, "site import, site save and settings save cannot bypass the large-fleet guard");

        var search = engine.StartSiteSearch(new SiteSearchRequest
        {
            Sites = new() { sites[0].Name }, Paths = new() { "/" }, Include = "release"
        });
        await Until(() => engine.SiteSearch(search.Id)!.Status != "running");
        check(engine.SiteSearch(search.Id)!.Status == "completed" && logins.GetValueOrDefault(sites[0].Name) == 1,
            "large-fleet pool still connects on demand for actual work");
        check(logins.Count == 1, "on-demand work leaves unrelated large-fleet sites disconnected");
    }

    private static async Task SharedBudget(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        var logins = new ConcurrentDictionary<string, int>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeCommand = async command =>
        {
            if (!command.StartsWith("USER ")) return;
            logins.AddOrUpdate(command[5..], 1, (_, count) => count + 1);
            await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
        };
        var sites = Enumerable.Range(1, 8).Select(i => Site(server, i)).ToArray();
        var engine = Engine(sites);
        var pending = new List<Task>();
        try
        {
            await Until(() => logins.Values.Sum() >= 12);
            var added = engine.AddSite(Site(server, 9));
            var saved = engine.SaveSite(sites[7].Name, sites[7]);
            engine.UpdateSettings(engine.Settings(false));
            pending.Add(Prime(engine));
            pending.Add(Prime(engine, added));
            pending.Add(Prime(engine, saved));
            await Task.Delay(200);
            check(logins.Count == 6 && logins.Values.Sum() == 12,
                "startup, import, site save and settings save share one six-site prewarm budget");

            for (var index = 10; index <= 17; index++)
                pending.Add(Prime(engine, engine.AddSite(Site(server, index))));
            release.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
            await Prime(engine).WaitAsync(TimeSpan.FromSeconds(10));
            check(logins.Count == 6 && logins.Values.Sum() == 12,
                "queued prewarms recheck fleet size after an import crosses sixteen sites");
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
}
