using WeaveFxp.Engine.Ftp;

internal static class UploadTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await Transfers(check, false);
        await Transfers(check, true);
    }

    private static async Task Transfers(Action<bool, string> check, bool tls)
    {
        await using var server = new TransferServer(tls);
        using var client = await FtpClient.DialAndLoginAsync(FtpClient.Config.FromSite(server.Site("upload-test", true, true)));
        foreach (var length in new[] { 0, 128, 200_000 })
        {
            var bytes = Enumerable.Range(0, length).Select(i => (byte)(i % 251)).ToArray();
            using var input = new MemoryStream(bytes);
            var name = $"upload-{length}.bin";
            var written = await client.StoreFromAsync("/" + name, input).WaitAsync(TimeSpan.FromSeconds(8));
            check(written == length && server.Files.TryGetValue(name, out var received) && received.SequenceEqual(bytes),
                $"{(tls ? "FTPS" : "FTP")} upload sends EOF before awaiting final response ({length} bytes)");
        }
        var (code, _) = await client.CommandAsync("NOOP");
        check(code == 200, "upload leaves pooled control connection synchronized");
    }
}
