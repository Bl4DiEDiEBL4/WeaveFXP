using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class MeshIntegration
{
    public static async Task Run(Action<bool, string> check)
    {
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
    public int Rejections => Volatile.Read(ref _rejections);
    public ConcurrentDictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Func<string, string?>? Reject { get; set; }
    public Func<string, string?>? Reply { get; set; }
    public Func<string, Task>? BeforeCommand { get; set; }
    public Func<string, List<RemoteEntry>>? Listing { get; set; }

    public TransferServer()
    {
        _listener.Start();
        _accept = Accept();
    }

    public Site Site(string name, bool download, bool upload) => new()
    {
        Name = name, Host = "127.0.0.1", Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
        TlsMode = TlsMode.Off, LoginSlots = 4, DownloadSlots = 3, UploadSlots = 3,
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
        using (client)
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
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
                        else foreach (var file in Files)
                            await writer.WriteLineAsync($"-rw-r--r-- 1 user group {file.Value.Length} Sep 05 12:00 {file.Key}");
                        await writer.WriteLineAsync("213 End");
                        break;
                    case "PASV":
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
                    case "RETR":
                    case "STOR":
                        await writer.WriteLineAsync("150 opening transfer");
                        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                        {
                            deadline.CancelAfter(TimeSpan.FromSeconds(3));
                            using var data = passive is not null
                                ? await passive.AcceptTcpClientAsync(deadline.Token) : new TcpClient();
                            if (passive is null) await data.ConnectAsync(active!.Address, active.Port, deadline.Token);
                            var name = arg.Split('/').Last();
                            if (parts[0] == "RETR") await data.GetStream().WriteAsync(Files[name], deadline.Token);
                            else
                            {
                                using var bytes = new MemoryStream();
                                await data.GetStream().CopyToAsync(bytes, deadline.Token);
                                Files[name] = bytes.ToArray();
                            }
                        }
                        passive?.Stop();
                        passive = null;
                        active = null;
                        await writer.WriteLineAsync("226 transfer complete");
                        break;
                    default: await writer.WriteLineAsync("200 OK"); break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { passive?.Stop(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_sessions);
        _stop.Dispose();
    }
}
