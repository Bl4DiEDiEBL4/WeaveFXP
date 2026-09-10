using System.Net;
using System.Net.Sockets;
using System.Text;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Ftp;

var passed = 0;
if (args.Contains("--search-fixture"))
{
    await using var fixture = SiteSearchTests.Fixture();
    fixture.Reject = command => command.StartsWith("CWD ") && (command.EndsWith(".nfo") || command.EndsWith(".sfv")) ? "550 not a directory" : null;
    fixture.BeforeCommand = command => command.StartsWith("RETR ") ? Task.Delay(TimeSpan.FromSeconds(60)) : Task.CompletedTask;
    Console.WriteLine("Search fixture FTP port: " + fixture.Site("fixture", true, true).Port);
    await Task.Delay(TimeSpan.FromMinutes(10));
    return;
}
void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}

var busy = new IOException("553 file.r00: file is being uploaded by another user");
Check(FxpTransfer.IsDestinationBusyError(busy), "WeaveFTPD 553 collision is destination occupancy");
Check(!FxpTransfer.IsDestinationDupeError(busy), "in-progress upload does not prove completeness");
var batchBusy = new IOException("553-X-DUPE: file.r00\r\n553 file.r00: file is being uploaded by another user");
Check(!FxpTransfer.IsDestinationDupeError(batchBusy), "X-DUPE batch cannot turn a busy upload into completion");
Check(FxpTransfer.IsDestinationDupeError(new IOException("553 file.r00: File exists.")), "plain completed duplicate remains recognized");
Check(!FxpTransfer.IsDestinationDupeError(new IOException("550 not allowed here")), "permission rejection is not destination coverage");
Check(!FxpTransfer.IsDestinationBusyError(new IOException("550 No Permission To Download A File Currently Being Uploaded")), "source upload-in-progress remains distinct");
Check(!FxpTransfer.IsDestinationDupeError(new IOException("550 PRET RETR target not found")), "missing source is not destination coverage");

var now = DateTime.UtcNow;
var pending = new MeshPendingDupe(now);
Check(!pending.IsConfirmedByListing(now.AddSeconds(-1), 100, 100), "listing begun before collision cannot confirm it");
Check(!pending.IsConfirmedByListing(now.AddSeconds(1), 50, 100), "partial destination stays pending");
Check(!pending.IsConfirmedByListing(now.AddSeconds(1), 100, 0), "unknown expected size stays pending");
Check(pending.IsConfirmedByListing(now.AddSeconds(1), 100, 100), "fresh full-size listing resolves occupancy");
Check(pending.RetryAt > now && pending.RetryAt <= now.AddSeconds(15), "abandoned upload suppression is bounded");

Check(MeshScheduling.WorkerCount(new[] { (10, 10, 1), (10, 1, 10) }) == 10, "asymmetric sites get ten workers rather than one");
Check(MeshScheduling.WorkerCount(new[] { (5, 5, 0), (5, 0, 5) }) == 5, "one-way routes use directional capacity");
Check(MeshScheduling.WorkerCount(new[] { (5, 5, 5), (5, 5, 5) }) == 5, "bidirectional workers respect total login capacity");

var board = new MeshScoreboard<string>();
board.Replace("release.r00", new() { ("A>C", 20), ("B>C", 10) });
board.Replace("release.sfv", new() { ("SFV", 100) });
Check(board.Ordered[0].Pick == "SFV", "protocol-critical file outranks bulk");
var cached = board.Ordered;
Check(ReferenceEquals(cached, board.Ordered), "unchanged scoreboard reuses its ordering");
board.Replace("release.sfv", new());
Check(board.Ordered.Count == 2 && board.Ordered[0].Pick == "A>C", "completed file removes all its candidates");
board.Replace("RELEASE.R00", new() { ("B>C", 10) });
Check(board.Ordered.Count == 1 && board.Ordered[0].Pick == "B>C", "alternate source survives replacement of a failed source");

await using var server = new LoginServer();
var pool = new WeaveEngine.SitePool(new FtpClient.Config
{
    Name = "loopback-test", Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 3
}, 6, 5, 5);
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var connections = new List<(string Owner, FtpClient Client)>();
try
{
    for (var i = 0; i < 5; i++)
    {
        var client = await pool.TryBorrowTransferAsync("race-a", true, timeout.Token, 2);
        Check(client is not null, $"single race can borrow transfer slot {i + 1}/5");
        connections.Add(("race-a", client!));
    }
    Check(await pool.TryBorrowTransferAsync("race-a", true, timeout.Token, 2) is null, "sixth transfer cannot exceed the cap");
    var control = await pool.BorrowAsync(timeout.Token);
    Check(server.Peak <= 6, "listing connection keeps physical logins within six");
    pool.Return(control);
    pool.SetMeshDemand("race-b", true, false);
    pool.ReturnTransfer("race-a", true, connections[^1].Client);
    connections.RemoveAt(connections.Count - 1);
    Check(await pool.TryBorrowTransferAsync("race-a", true, timeout.Token, 2) is null, "busy race yields to actual newcomer demand");
    var newcomer = await pool.TryBorrowTransferAsync("race-b", true, timeout.Token, 2);
    Check(newcomer is not null, "newcomer acquires released slot");
    connections.Add(("race-b", newcomer!));
    var old = connections.First(x => x.Owner == "race-a");
    pool.ReturnTransfer(old.Owner, true, old.Client);
    connections.Remove(old);
    var second = await pool.TryBorrowTransferAsync("race-b", true, timeout.Token, 2);
    Check(second is not null, "two active races can fill all five slots without permanent reserve");
    connections.Add(("race-b", second!));
    pool.SetMeshDemand("race-b", false, false);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try { await pool.BorrowTransferAsync("cancelled", true, cancelled.Token); throw new Exception("expected cancellation"); }
    catch (OperationCanceledException) { Check(true, "cancelled waiter exits"); }
    foreach (var c in connections) pool.ReturnTransfer(c.Owner, true, c.Client);
    connections.Clear();
    Check(pool.CanBorrowTransfer("race-a", true, 2), "cancelled waiter does not retain ownership");
    Check(pool.LimitTransferSlots(true, 2), "server-advertised directional limit can be learned");
    for (var i = 0; i < 2; i++)
    {
        var c = await pool.TryBorrowTransferAsync("race-a", true, timeout.Token, 2);
        Check(c is not null, "learned capacity remains usable");
        connections.Add(("race-a", c!));
    }
    Check(await pool.TryBorrowTransferAsync("race-a", true, timeout.Token, 2) is null, "learned limit rejects excess transfer");
}
finally
{
    foreach (var c in connections) pool.ReturnTransfer(c.Owner, true, c.Client);
    await pool.SweepAsync(TimeSpan.FromSeconds(-1), TimeSpan.Zero);
}
var pairSource = new WeaveEngine.SitePool(new FtpClient.Config
{
    Name = "pair-source", Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 3
}, 2, 1, 1);
var pairDestination = new WeaveEngine.SitePool(new FtpClient.Config
{
    Name = "pair-destination", Host = "127.0.0.1", Port = server.Port, TimeoutSeconds = 3
}, 2, 1, 1);
using (var occupied = pairDestination.TryReserveTransferSlot("existing", false))
{
    var reservedSource = pairSource.TryReserveTransferSlot("next", true);
    Check(reservedSource is not null, "source slot can be reserved without opening a connection");
    Check(pairDestination.TryReserveTransferSlot("next", false) is null, "pair reservation detects occupied destination");
    reservedSource!.Dispose();
    reservedSource.Dispose();
    Check(pairSource.CanBorrowTransfer("another", true), "failed pair releases source reservation exactly once");
}
using (var reserved = pairSource.TryReserveTransferSlot("cancel", true))
{
    using var stopped = new CancellationTokenSource();
    stopped.Cancel();
    try { await reserved!.OpenAsync(stopped.Token); throw new Exception("expected reservation cancellation"); }
    catch (OperationCanceledException) { Check(true, "cancelled reserved transfer does not start login"); }
}
Check(pairSource.CanBorrowTransfer("next", true), "cancelled unopened reservation restores capacity");
using (var reserved = pairSource.TryReserveTransferSlot("handoff", true))
{
    var client = await reserved!.OpenAsync(CancellationToken.None);
    reserved.Dispose();
    Check(!pairSource.CanBorrowTransfer("other", true), "opened reservation remains owned by live client");
    pairSource.ReturnTransfer("handoff", true, client);
}
Check(pairSource.CanBorrowTransfer("next", true), "returned client restores handed-off permits");
await pairSource.SweepAsync(TimeSpan.FromSeconds(-1), TimeSpan.Zero);
GlobalScoreboardTests.Run(Check);
await ManualQueueTests.Run(Check);
await SiteSearchTests.Run(Check);
await MeshIntegration.Run(Check);
Console.WriteLine($"{passed} regression checks passed.");

sealed class LoginServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _sessions = new();
    private readonly Task _accept;
    private int _active;
    public int Peak { get; private set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public LoginServer()
    {
        _listener.Start();
        _accept = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _sessions.Add(ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        var active = Interlocked.Increment(ref _active);
        Peak = Math.Max(Peak, active);
        using (client)
        try
        {
            using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
            await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
            await writer.WriteLineAsync("220 regression FTP");
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
                await writer.WriteLineAsync(line.StartsWith("USER ") ? "331 password" : line.StartsWith("PASS ") ? "230 logged in" : "200 OK");
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        finally { Interlocked.Decrement(ref _active); }
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
