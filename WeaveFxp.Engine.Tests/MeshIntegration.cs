using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Collections;
using System.Reflection;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class MeshIntegration
{
    public static async Task Run(Action<bool, string> check)
    {
        await Bidirectional(check, spread: false);
        await Bidirectional(check, spread: true);
        await using var source = new TransferServer();
        await using var alternate = new TransferServer();
        await using var destination = new TransferServer();
        var payload = new byte[4096];
        var sfv = Encoding.ASCII.GetBytes("payload.r00 00000000\r\n");
        foreach (var server in new[] { source, alternate, destination }) server.Files["release.sfv"] = sfv;
        source.Files["payload.r00"] = payload;
        alternate.Files["payload.r00"] = payload;
        destination.Files["payload.r00"] = new byte[10];
        destination.Reject = cmd => cmd == "PRET STOR payload.r00" ? "553 payload.r00: file is being uploaded by another user" : null;

        var path = Path.Combine(Path.GetTempPath(), "weave-mesh-tests-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 100;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(source.Site("source", true, false));
        engine.AddSite(alternate.Site("alternate", true, false));
        engine.AddSite(destination.Site("destination", false, true));
        Job Start() => engine.StartFxp(new TransferRequest
        {
            Race = true, FromSite = "source", ToSite = "destination", SourcePath = "/release", DestPath = "/release",
            MeshSites = new() { "source", "alternate", "destination" }, ViaApi = true
        });
        var job = Start();
        try
        {
            await Until(() => destination.Rejections > 0, engine, job.Id);
            await Task.Delay(250);
            check(engine.Job(job.Id)!.State == JobState.Running, "mesh does not finish on a 553 in-progress duplicate");
            check(destination.Rejections == 1, "other mesh sources do not retry an occupied destination");
            destination.Reject = null;
            destination.Files["payload.r00"] = payload;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(engine.Job(job.Id)!.State == JobState.Succeeded, "fresh destination listing finishes externally completed mesh");

            destination.Files.TryRemove("payload.r00", out _);
            alternate.Files.TryRemove("payload.r00", out _);
            source.Reject = cmd => cmd == "PRET RETR payload.r00" ? "550 source read denied" : null;
            job = Start();
            await Until(() => source.Rejections > 0, engine, job.Id);
            alternate.Files["payload.r00"] = payload;
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            check(source.Rejections > 0, "preferred source actually fails during mesh test");
            check(engine.Job(job.Id)!.State == JobState.Succeeded && destination.Files["payload.r00"].Length == payload.Length,
                "alternate source completes transfer after preferred source failure");
            check(engine.Job(job.Id)!.Files.Any(x => x.FromSite == "alternate" && x.Name == "payload.r00" && x.Status == "done"),
                "successful alternate route is recorded");

            destination.Files.TryRemove("payload.r00", out _);
            var previousRejects = destination.Rejections;
            destination.Reject = cmd => cmd == "PRET STOR payload.r00" ? "550 not allowed here" : null;
            job = Start();
            await Until(() => destination.Rejections > previousRejects, engine, job.Id);
            await Task.Delay(200);
            check(engine.Job(job.Id)!.State != JobState.Succeeded, "destination permission refusal cannot falsely complete mesh");
            check(!engine.Job(job.Id)!.Files.Any(x => x.Name == "payload.r00" && x.Status == "dupe"),
                "permission refusal is not displayed as a duplicate");
        }
        finally
        {
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task Bidirectional(Action<bool, string> check, bool spread)
    {
        await using var left = new TransferServer();
        await using var right = new TransferServer();
        var leftPayload = Enumerable.Repeat((byte)17, 4096).ToArray();
        var rightPayload = Enumerable.Repeat((byte)29, 2048).ToArray();
        left.Files["left.r00"] = leftPayload;
        right.Files["right.r00"] = rightPayload;
        var sfv = Encoding.ASCII.GetBytes("left.r00 00000000\r\nright.r00 00000000\r\n");
        left.Files["release.sfv"] = sfv;
        right.Files["release.sfv"] = sfv;
        var transferring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var server in new[] { left, right })
            server.BeforeCommand = async command =>
            {
                if (!command.StartsWith("PRET STOR ")) return;
                transferring.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            };

        var path = Path.Combine(Path.GetTempPath(), "weave-two-site-tests-" + Guid.NewGuid().ToString("N"), "state.json");
        var engine = new WeaveEngine(path);
        var settings = engine.Settings(false);
        settings.RacePollIntervalMs = 25;
        settings.RaceMaxIdleCycles = 100;
        settings.GlobalSkiplist.Clear();
        engine.UpdateSettings(settings);
        engine.AddSite(left.Site("left", true, true));
        engine.AddSite(right.Site("right", true, true));
        var job = spread
            ? engine.StartSpread(new SpreadRequest
            {
                Race = true, FromSite = "left", ToSites = new() { "right" },
                SourcePath = "/left-release", DestPath = "/right-release", ViaApi = true
            }).Jobs.Single()
            : engine.StartFxp(new TransferRequest
            {
                Race = true, FromSite = "left", ToSite = "right",
                SourcePath = "/left-release", DestPath = "/right-release",
                MeshSites = new() { "left", "right" }, ViaApi = true
            });
        try
        {
            await transferring.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var board = typeof(WeaveEngine).GetField("_meshScoreboard", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
            var sync = board.GetType().GetField("_sync", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(board)!;
            lock (sync)
            {
                var owners = (IDictionary)board.GetType().GetField("_owners", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(board)!;
                check(owners.Contains(job.Id), $"two-site race registers with the global scoreboard (spread={spread})");
            }
            release.TrySetResult();
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
            var completed = engine.Job(job.Id)!;
            check(completed.State == JobState.Succeeded, $"two-site bidirectional race completes (spread={spread})");
            check(left.Files.TryGetValue("right.r00", out var receivedLeft) && receivedLeft.SequenceEqual(rightPayload) &&
                right.Files.TryGetValue("left.r00", out var receivedRight) && receivedRight.SequenceEqual(leftPayload),
                $"two-site race repairs complementary files in both directions (spread={spread})");
            check(completed.Files.Any(file => file.Status == "done" && file.FromSite == "left" && file.ToSite == "right") &&
                completed.Files.Any(file => file.Status == "done" && file.FromSite == "right" && file.ToSite == "left"),
                $"two-site race records both transfer routes (spread={spread})");
        }
        finally
        {
            release.TrySetResult();
            if (engine.Job(job.Id) is { Terminal: false }) engine.CancelJob(job.Id);
            await Until(() => engine.Job(job.Id)!.Terminal, engine, job.Id);
        }
    }

    private static async Task Until(Func<bool> condition, WeaveEngine engine, string job)
    {
        var deadline = DateTime.UtcNow.AddSeconds(12);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Exception("Mesh timed out: " + string.Join("\n", engine.Job(job)!.Events.Select(x => x.Message)));
            await Task.Delay(20);
        }
    }
}

internal sealed class TransferServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = new();
    private readonly Task _accept;
    private int _rejections;
    private readonly X509Certificate2? _certificate;
    public int Rejections => Volatile.Read(ref _rejections);
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, string?>? Reject { get; set; }
    public Func<string, string?>? Reply { get; set; }
    public Func<string, string?>? TransferFinalReply { get; set; }
    public Func<string, Task>? BeforeTransferFinalReply { get; set; }
    public Func<string, Task>? BeforeCommand { get; set; }
    public Func<string, List<RemoteEntry>>? Listing { get; set; }
    public Func<Task>? AfterListingSnapshot { get; set; }

    public TransferServer(bool tls = false)
    {
        if (tls)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            _certificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));
        }
        _listener.Start();
        _accept = Accept();
    }

    public Site Site(string name, bool download, bool upload) => new()
    {
        Name = name, Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
        TlsMode = _certificate is null ? TlsMode.Off : TlsMode.Implicit, LoginSlots = 4, DownloadSlots = 3, UploadSlots = 3,
        AllowDownload = download, AllowUpload = upload, UsePret = true, UseXdupe = false,
        SscnSupported = false, CpsvSupported = false, ListCommand = "STAT -l", TimeoutSeconds = 3
    };

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
                _sessions.Add(Serve(await _listener.AcceptTcpClientAsync(_stop.Token)));
        }
        catch (OperationCanceledException) { }
    }

    private async Task Serve(TcpClient client)
    {
        TcpListener? passive = null;
        IPEndPoint? active = null;
        var cwd = "/";
        var sscn = false;
        var cpsv = false;
        using (client)
        try
        {
            using var control = await ServerStream(client);
            using var reader = new StreamReader(control, Encoding.ASCII);
            await using var writer = new StreamWriter(control, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 mesh regression FTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (BeforeCommand is { } before) await before(line);
                if (Reject?.Invoke(line) is { } rejection)
                {
                    Interlocked.Increment(ref _rejections);
                    await writer.WriteLineAsync(rejection);
                    continue;
                }
                var parts = line.Split(' ', 2);
                if (Reply?.Invoke(line) is { } reply)
                {
                    await writer.WriteLineAsync(reply);
                    continue;
                }
                var arg = parts.Length > 1 ? parts[1] : "";
                switch (parts[0])
                {
                    case "USER": await writer.WriteLineAsync("331 password"); break;
                    case "PASS": await writer.WriteLineAsync("230 logged in"); break;
                    case "CWD": cwd = arg; await writer.WriteLineAsync("250 directory changed"); break;
                    case "STAT":
                        await writer.WriteLineAsync("213-listing");
                        if (Listing is { } listing)
                        {
                            var pathStart = arg.IndexOf('/');
                            foreach (var entry in listing(pathStart >= 0 ? arg[pathStart..] : cwd))
                                await writer.WriteLineAsync($"{(entry.Type == "dir" ? 'd' : entry.Type == "link" ? 'l' : '-')}rw-r--r-- 1 user group {entry.Size} Sep 05 12:00 {entry.Name}");
                        }
                        else
                        {
                            var snapshot = Files.ToArray();
                            if (AfterListingSnapshot is { } afterSnapshot) await afterSnapshot();
                            foreach (var file in snapshot)
                                await writer.WriteLineAsync($"-rw-r--r-- 1 user group {file.Value.Length} Sep 05 12:00 {file.Key}");
                        }
                        await writer.WriteLineAsync("213 End");
                        break;
                    case "SSCN":
                        sscn = arg == "ON";
                        await writer.WriteLineAsync("200 SSCN accepted");
                        break;
                    case "CPSV":
                    case "PASV":
                        cpsv = parts[0] == "CPSV";
                        passive?.Stop();
                        passive = new TcpListener(IPAddress.Loopback, 0);
                        passive.Start();
                        var port = ((IPEndPoint)passive.LocalEndpoint).Port;
                        await writer.WriteLineAsync($"227 Entering Passive Mode (127,0,0,1,{port / 256},{port % 256})");
                        break;
                    case "PORT":
                        var address = arg.Split(',').Select(int.Parse).ToArray();
                        active = new IPEndPoint(IPAddress.Loopback, address[4] * 256 + address[5]);
                        await writer.WriteLineAsync("200 PORT accepted");
                        break;
                    case "SIZE":
                        if (Files.TryGetValue(arg.Split('/').Last(), out var sized))
                            await writer.WriteLineAsync("213 " + sized.Length);
                        else await writer.WriteLineAsync("550 file not found");
                        break;
                    case "RETR":
                    case "STOR":
                    case "LIST":
                        byte[]? download = null;
                        if (parts[0] == "LIST") download = Encoding.ASCII.GetBytes(string.Join("\r\n",
                            Files.Select(f => $"-rw-r--r-- 1 user group {f.Value.Length} Sep 05 12:00 {f.Key}")) + "\r\n");
                        if (parts[0] == "RETR" && !Files.TryGetValue(arg.Split('/').Last(), out download))
                        {
                            await writer.WriteLineAsync("550 file not found");
                            break;
                        }
                        await writer.WriteLineAsync("150 opening transfer");
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(3));
                            using var data = passive is not null
                                ? await passive.AcceptTcpClientAsync(deadline.Token) : new TcpClient();
                            if (passive is null) await data.ConnectAsync(active!.Address, active.Port, deadline.Token);
                            var name = arg.Split('/').Last();
                            using var dataStream = await ServerStream(data, sscn || cpsv);
                            if (parts[0] != "STOR")
                            {
                                await dataStream.WriteAsync(download!, deadline.Token);
                                if (dataStream is SslStream tls) await tls.ShutdownAsync();
                            }
                            else
                            {
                                using var bytes = new MemoryStream();
                                await dataStream.CopyToAsync(bytes, deadline.Token);
                                Files[name] = bytes.ToArray();
                            }
                        }
                        passive?.Stop();
                        passive = null;
                        active = null;
                        cpsv = false;
                        if (BeforeTransferFinalReply is { } finalDelay) await finalDelay(line);
                        await writer.WriteLineAsync(TransferFinalReply?.Invoke(line) ?? "226 transfer complete");
                        break;
                    default: await writer.WriteLineAsync("200 OK"); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        // A cancelled peer can close its passive listener before this fixture
        // connects. That ends the session, just like a broken control stream.
        catch (SocketException) { }
        finally { passive?.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_sessions);
        _certificate?.Dispose();
        _stop.Dispose();
    }

    private async Task<Stream> ServerStream(TcpClient client, bool tlsClient = false)
    {
        if (_certificate is null) return client.GetStream();
        var ssl = new SslStream(client.GetStream(), false, (_, _, _, _) => true);
        try
        {
            if (tlsClient)
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, _stop.Token);
            else
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate }, _stop.Token);
            return ssl;
        }
        catch { ssl.Dispose(); throw; }
    }
}
