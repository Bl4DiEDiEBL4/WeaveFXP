using WeaveFxp.Engine.Ftp;
using WeaveFxp.Engine.Models;

internal static class TlsRoleTests
{
    public static async Task Run(Action<bool, string> check)
    {
        foreach (var operation in new[] { "RETR", "STOR", "LIST" })
        {
            await using var server = new TransferServer(tls: true);
            server.Files["file.bin"] = new byte[] { 1, 2, 3 };
            var commands = new List<string>();
            server.BeforeCommand = command => { commands.Add(command); return Task.CompletedTask; };
            var config = FtpClient.Config.FromSite(server.Site("local", true, true));
            config.UseSscn = true;
            config.ListCommand = "LIST";
            using var client = await FtpClient.DialAndLoginAsync(config);
            await client.SetSscnAsync(true);
            using var bytes = new MemoryStream(new byte[] { 1, 2, 3 });
            if (operation == "RETR")
            {
                using var output = new MemoryStream();
                await client.RetrieveToAsync("file.bin", output);
                check(output.ToArray().SequenceEqual(bytes.ToArray()), "TLS download bytes after SSCN ON");
            }
            else if (operation == "STOR") await client.StoreFromAsync("uploaded.bin", bytes);
            else check((await client.ListAsync("/")).Count == 1, "TLS LIST after SSCN ON");
            check(commands.IndexOf("SSCN OFF") > commands.IndexOf("SSCN ON") &&
                commands.IndexOf("SSCN OFF") < commands.IndexOf("PASV"), operation + " resets TLS role before PASV");
            check((await client.CommandAsync("NOOP")).code == 200, operation + " leaves control synchronized");
        }

        foreach (var rejectCpsv in new[] { false, true })
        foreach (var sourcePassive in new[] { false, true })
        {
            await using var source = new TransferServer(tls: true);
            await using var target = new TransferServer(tls: true);
            source.Files["file.bin"] = new byte[] { 7, 8, 9 };
            var commands = new List<string>();
            var passiveServer = sourcePassive ? source : target;
            passiveServer.BeforeCommand = command => { commands.Add(command); return Task.CompletedTask; };
            if (rejectCpsv) passiveServer.Reject = command => command == "CPSV" ? "500 CPSV unsupported" : null;
            var sc = FtpClient.Config.FromSite(source.Site("source", true, false));
            var dc = FtpClient.Config.FromSite(target.Site("target", false, true));
            sc.UseSscn = dc.UseSscn = true;
            sc.FxpPassiveSide = sourcePassive ? FxpPassiveSide.Source : FxpPassiveSide.Destination;
            sc.FxpMode = dc.FxpMode = FxpMode.CpsvSsc;
            using var src = await FtpClient.DialAndLoginAsync(sc);
            using var dst = await FtpClient.DialAndLoginAsync(dc);
            await FxpTransfer.TransferSingleAsync(src, dst, dc, "/release/file.bin", "/release/file.bin",
                (_, _) => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            check(target.Files["file.bin"].SequenceEqual(source.Files["file.bin"]), $"TLS FXP payload (CPSV rejected={rejectCpsv}, source passive={sourcePassive})");
            if (rejectCpsv) check(commands.IndexOf("SSCN ON") > commands.IndexOf("CPSV") &&
                commands.IndexOf("SSCN ON") < commands.IndexOf("PASV"), "CPSV fallback restores roles before PASV");
            using var local = new MemoryStream();
            await (sourcePassive ? src : dst).RetrieveToAsync("file.bin", local);
            check(local.ToArray().SequenceEqual(source.Files["file.bin"]), "local TLS download reuses FXP session safely");
        }
    }
}
