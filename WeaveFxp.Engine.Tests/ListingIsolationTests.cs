using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;

internal static class ListingIsolationTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await StaleReplies(check);
        foreach (var code in new[] { 200, 225, 226, 250, 257 })
            await WrongStatReply(check, code);
        foreach (var code in new[] { 211, 212, 213 })
            await ValidStatReply(check, code);
        await PooledDirectories(check);
        await ExplicitStatTarget(check);
        await SpacedNames(check);
    }

    private static async Task ExplicitStatTarget(Action<bool, string> check)
    {
        foreach (var command in new[] { "STAT -l", "STAT -la", "STAT", "STAT -l .", "STAT -l /explicit" })
            await ExplicitStatTarget(check, command);
    }

    private static async Task ExplicitStatTarget(Action<bool, string> check, string command)
    {
        await using var server = new TransferServer();
        // WeaveFTPd used to let a previous PRET LIST redirect an unqualified STAT.
        var expected = command == "STAT -l /explicit" || command == "STAT -l ." ? command : command + " .";
        server.Reply = line => line.StartsWith("STAT")
            ? $"213-listing\r\n-rw-r--r-- 1 user group 1 Oct 22 03:46 {(line == expected ? "own.r03" : "foreign.r03")}\r\n213 End"
            : null;
        var config = Config(server);
        config.ListCommand = command;
        using var client = await FtpClient.DialAndLoginAsync(config);
        await client.CommandAsync("PRET LIST /other-release");
        var entries = await client.ListAsync("/release");
        check(entries.Count == 1 && entries[0].Name == "own.r03" && entries[0].Path == "/release/own.r03",
            $"CWD-based {command} keeps an explicit target rather than inheriting another PRET directory");
    }

    private static async Task SpacedNames(Action<bool, string> check)
    {
        var entries = FtpClient.ParseList("drwxr-xr-x 1 user group 4096 Oct 22 03:46 [Electronic  2026]\r\n" +
            "-rw-r--r-- 1 user group 1 Oct 22 03:46 real  one byte.bin\r\n" +
            "lrwxr-xr-x 1 user group 0 Oct 22 03:46 Sample  Link -> /Sample  Target\r\n", "/release");
        check(entries.Count == 3 && entries[0].Name == "[Electronic  2026]" &&
            entries[1].Name == "real  one byte.bin" && entries[2].Name == "Sample  Link" && entries[2].LinkTarget == "/Sample  Target",
            "Unix listing preserves repeated spaces in directories, one-byte filenames and symlinks");
        await using var server = new TransferServer();
        server.Reply = command => command.StartsWith("STAT")
            ? "213-listing\r\ndrwxr-xr-x 1 user group 4096 Oct 22 03:46 [Electronic  2026]\r\n213 End"
            : null;
        using var client = await FtpClient.DialAndLoginAsync(Config(server));
        var listed = await client.ListAsync("/release");
        var cwd = "";
        server.BeforeCommand = command => { if (command.StartsWith("CWD ")) cwd = command; return Task.CompletedTask; };
        await client.EnsureCwdAsync(listed.Single().Path);
        check(cwd == "CWD /release/[Electronic  2026]", "listing-to-CWD round trip preserves exact spaced pathname on the wire");
    }

    private static FtpClient.Config Config(TransferServer server)
    {
        var config = FtpClient.Config.FromSite(server.Site("listing", true, false));
        config.CwdBeforeStatListing = true;
        return config;
    }

    private static async Task StaleReplies(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        server.Listing = path => new() { new() { Name = path == "/release-a" ? "a.r03" : "b.r03", Type = "file", Size = 1 } };
        // A transfer final and an ABOR acknowledgement left behind on a reused
        // control channel must not stand in for the next CWD and STAT replies.
        server.Reply = command => command == "NOOP" ? "200 NOOP\r\n226 old transfer complete\r\n225 old abort complete" : null;
        using var client = await FtpClient.DialAndLoginAsync(Config(server));
        await client.CommandAsync("NOOP");
        var rejected = false;
        var contaminated = false;
        try
        {
            await client.ListAsync("/release-a");
            var entries = await client.ListAsync("/release-b");
            contaminated = entries.Any(e => e.Name == "a.r03" && e.Path == "/release-b/a.r03" && e.Size == 1);
        }
        catch (IOException) { rejected = true; }
        check(!contaminated, "stale control replies cannot relabel release A's 1-byte entry as release B");
        check(rejected, "unexpected CWD reply rejects the desynchronized connection");
    }

    private static async Task WrongStatReply(Action<bool, string> check, int code)
    {
        await using var server = new TransferServer();
        server.Reply = command => command.StartsWith("STAT") ? $"{code}-not a listing response\r\n-rw-r--r-- 1 user group 1 Oct 22 03:46 foreign.r03\r\n{code} End" : null;
        using var client = await FtpClient.DialAndLoginAsync(Config(server));
        var rejected = false;
        try { await client.ListAsync("/release"); }
        catch (IOException) { rejected = true; }
        check(rejected, $"STAT rejects unrelated success reply {code}, including listing-shaped text");
    }

    private static async Task ValidStatReply(Action<bool, string> check, int code)
    {
        await using var server = new TransferServer();
        server.Reply = command => command.StartsWith("STAT") ? $"{code}-listing\r\n-rw-r--r-- 1 user group 1 Oct 22 03:46 own.r03\r\n{code} End" : null;
        using var client = await FtpClient.DialAndLoginAsync(Config(server));
        var entries = await client.ListAsync("/release");
        check(entries.Count == 1 && entries[0].Name == "own.r03" && entries[0].Size == 1,
            $"valid STAT {code} preserves legitimate 1-byte upload hints");
    }

    private static async Task PooledDirectories(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        server.Listing = path => new() { new() { Name = path.TrimStart('/') + ".r03", Type = "file", Size = 1 } };
        var pool = new WeaveEngine.SitePool(Config(server), 4, 3, 3);
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async release =>
            {
                var path = "/release-" + release;
                for (var i = 0; i < 30; i++)
                {
                    var client = await pool.BorrowAsync(CancellationToken.None);
                    try
                    {
                        var entries = await client.ListAsync(path);
                        if (entries.Count != 1 || entries[0].Path != path + path + ".r03") return false;
                    }
                    finally { pool.Return(client); }
                    await Task.Yield();
                }
                return true;
            }));
            check(results.All(x => x), "240 concurrent listings across eight releases keep pooled CWD state isolated");
        }
        finally { pool.DisposeAll(); }
    }
}
