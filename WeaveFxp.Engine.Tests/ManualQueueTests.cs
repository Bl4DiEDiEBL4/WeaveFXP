using System.Collections.Concurrent;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class ManualQueueTests
{
    public static async Task Run(Action<bool, string> check)
    {
        var queue = new ManualTransferQueue();
        var lane = new ManualTransferQueue.Limit("local:test", 1);
        var held = queue.Enqueue("held", default, true, lane);
        check(!held.IsCompleted && queue.Positions().ContainsKey("held"), "queue-only job stays pending on an idle queue");
        using (await queue.Enqueue("immediate", default, lane))
            check(!held.IsCompleted, "held job reserves no capacity and does not start after another transfer");
        queue.SetPaused("held", false);
        using (await held.WaitAsync(TimeSpan.FromSeconds(2)))
            check(true, "explicit start releases a held job");
        using var heldCancel = new CancellationTokenSource();
        var cancelledHeld = queue.Enqueue("held-cancel", heldCancel.Token, true, lane);
        heldCancel.Cancel();
        try { using var unexpected = await cancelledHeld; check(false, "held cancellation cannot start a job"); }
        catch (OperationCanceledException) { check(!queue.Positions().ContainsKey("held-cancel"), "held job can be cancelled without starting"); }
        using var active = await queue.Enqueue("active", default, lane);
        var first = queue.Enqueue("first", default, lane);
        var second = queue.Enqueue("second", default, lane);
        var third = queue.Enqueue("third", default, lane);
        check(!first.IsCompleted && !second.IsCompleted, "manual transfers wait behind the active site job");
        check(!queue.Move("active", 1) && !queue.Move("first", -1) && !queue.Move("third", 1), "active jobs and queue boundaries cannot move");
        check(!queue.Move("first", 2), "invalid move direction is rejected");
        check(queue.Move("third", -1) && queue.Move("first", 1), "manual queue supports both move directions");
        check(queue.Positions()["third"] == 0 && queue.Positions()["first"] == 1, "display order matches the manual scheduler");
        using (await queue.Enqueue("other-site", default, new ManualTransferQueue.Limit("local:other", 1)))
            check(true, "unrelated local site can work while the first site is queued");
        active.Dispose();
        using (await third.WaitAsync(TimeSpan.FromSeconds(2)))
            check(!first.IsCompleted && !second.IsCompleted, "moved job starts before its former predecessors");
        using (await first.WaitAsync(TimeSpan.FromSeconds(2)))
            check(!second.IsCompleted, "remaining jobs keep their relative order");
        (await second.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        using var blocker = await queue.Enqueue("blocker", default, lane);
        using var cancel = new CancellationTokenSource();
        var cancelled = queue.Enqueue("cancelled", cancel.Token, lane);
        cancel.Cancel();
        try { using var unexpected = await cancelled; check(false, "cancelled queue entry cannot start"); }
        catch (OperationCanceledException) { check(!queue.Positions().ContainsKey("cancelled"), "cancelled queue entry is removed immediately"); }
        using var bulkCancel = new CancellationTokenSource();
        var bulk = queue.Enqueue("bulk", bulkCancel.Token, lane);
        using (queue.SuspendDispatch())
        {
            blocker.Dispose();
            check(!bulk.IsCompleted, "bulk removal suspends dispatch while active jobs release capacity");
            bulkCancel.Cancel();
        }
        try { using var unexpected = await bulk; check(false, "bulk cancelled job cannot start"); }
        catch (OperationCanceledException) { check(queue.Positions().Count == 0, "bulk removal leaves no pending entries"); }
        using var after = await queue.Enqueue("after", default, lane);
        check(true, "queue remains usable after bulk removal");

        var fxp = new ManualTransferQueue.Limit("fxp", 2);
        var batch = new ManualTransferQueue.Limit("batch:one", 1);
        using var fxp1 = await queue.Enqueue("fxp1", default, fxp, batch);
        var fxp2 = queue.Enqueue("fxp2", default, fxp, batch);
        using var fxp3 = await queue.Enqueue("fxp3", default, fxp);
        var fxp4 = queue.Enqueue("fxp4", default, fxp);
        check(!fxp2.IsCompleted && !fxp4.IsCompleted, "FXP queue enforces both global and spread batch limits");
        fxp1.Dispose();
        using (await fxp2.WaitAsync(TimeSpan.FromSeconds(2)))
            check(!fxp4.IsCompleted, "first eligible FXP job gets the released capacity");
        (await fxp4.WaitAsync(TimeSpan.FromSeconds(2))).Dispose();
        await Integration(check);
    }

    private static async Task Integration(Action<bool, string> check)
    {
        await using var server = new TransferServer();
        var order = new ConcurrentQueue<string>();
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Reject = cmd => cmd.StartsWith("CWD /file-") ? "550 not a directory" : null;
        server.BeforeCommand = async command =>
        {
            if (!command.StartsWith("RETR ")) return;
            var name = command.Split('/').Last();
            order.Enqueue(name);
            if (name == "file-block.bin")
            {
                blocked.TrySetResult();
                await release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
        };
        foreach (var name in new[] { "block", "first", "second", "third" }) server.Files[$"file-{name}.bin"] = new byte[64];
        var root = Path.Combine(Path.GetTempPath(), "weave-queue-tests-" + Guid.NewGuid().ToString("N"));
        var engine = new WeaveEngine(Path.Combine(root, "state.json"));
        engine.AddSite(server.Site("queue-test", true, true));
        Job Start(string name) => engine.StartDownload(new DownloadRequest
        {
            Site = "queue-test", SourcePath = $"/file-{name}.bin", DestPath = Path.Combine(root, name + ".bin")
        });
        var jobs = new List<Job>();
        try
        {
            jobs.Add(Start("block"));
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
            jobs.Add(Start("first"));
            jobs.Add(Start("second"));
            jobs.Add(Start("third"));
            check(engine.MoveManualJob(jobs[3].Id, -1) && engine.MoveManualJob(jobs[3].Id, -1), "engine accepts moving a queued download to the front");
            release.TrySetResult();
            await Until(() => jobs.All(job => engine.Job(job.Id)!.Terminal));
            check(jobs.All(job => engine.Job(job.Id)!.State == JobState.Succeeded), "reordered local downloads complete successfully");
            check(order.SequenceEqual(new[] { "file-block.bin", "file-third.bin", "file-first.bin", "file-second.bin" }), "actual FTP RETR order follows manual queue changes");
            check(engine.RemoveManualJobs(jobs.Select(job => job.Id)) == 4 && jobs.All(job => engine.Job(job.Id) is null), "remove all removes every completed manual job");

            var heldDownload = engine.StartDownload(new DownloadRequest
            {
                Site = "queue-test", SourcePath = "/file-first.bin", DestPath = Path.Combine(root, "held.bin")
            }, queueOnly: true);
            jobs.Add(heldDownload);
            await Task.Delay(150);
            check(engine.Job(heldDownload.Id) is { State: JobState.Queued, Paused: true } && order.Count == 4,
                "queue-only download does not issue RETR until started");
            engine.ResumeJob(heldDownload.Id);
            await Until(() => engine.Job(heldDownload.Id)!.Terminal);
            check(engine.Job(heldDownload.Id)!.State == JobState.Succeeded, "starting held download completes normally");

            var heldUpload = engine.StartUpload(new UploadRequest
            {
                Site = "queue-test", SourcePath = Path.Combine(root, "held.bin"), DestPath = "/held-upload.bin"
            }, queueOnly: true);
            jobs.Add(heldUpload);
            await Task.Delay(150);
            check(engine.Job(heldUpload.Id) is { State: JobState.Queued, Paused: true } && !server.Files.ContainsKey("held-upload.bin"),
                "queue-only upload does not start on an idle site");
            engine.ResumeJob(heldUpload.Id);
            await Until(() => engine.Job(heldUpload.Id)!.Terminal);
            check(engine.Job(heldUpload.Id)!.State == JobState.Succeeded && server.Files["held-upload.bin"].Length == 64,
                "starting held upload completes with server-confirmed data");
            engine.RemoveManualJobs(new[] { heldDownload.Id, heldUpload.Id });

            order.Clear();
            blocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            jobs.Clear();
            jobs.Add(Start("block"));
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(8));
            jobs.Add(Start("first"));
            jobs.Add(Start("second"));
            engine.AddSite(server.Site("queue-target", true, true));
            var heldFxp = engine.StartFxp(new TransferRequest
            {
                FromSite = "queue-test", ToSite = "queue-target", SourcePath = "/release", DestPath = "/release", DryRun = true
            }, queueOnly: true);
            await Task.Delay(150);
            check(engine.Job(heldFxp.Id) is { State: JobState.Queued, Paused: true }, "queue-only FXP stays pending until explicit start");
            engine.ResumeJob(heldFxp.Id);
            await Until(() => engine.Job(heldFxp.Id)!.Terminal);
            check(engine.Job(heldFxp.Id)!.State == JobState.Succeeded, "held FXP starts through the existing queue action");
            engine.RemoveManualJobs(new[] { heldFxp.Id });
            var race = engine.StartFxp(new TransferRequest
            {
                FromSite = "queue-test", ToSite = "queue-target", SourcePath = "/release", DestPath = "/release", Race = true, DryRun = true
            });
            await Until(() => engine.Job(race.Id)!.Terminal);
            check(engine.RemoveManualJobs(new[] { race.Id }) == 0 && engine.Job(race.Id) is not null,
                "manual remove all excludes race jobs");
            check(engine.RemoveManualJobs(jobs.Select(job => job.Id)) == 3, "remove all also removes active and pending manual jobs");
            release.TrySetResult();
            await Task.Delay(150);
            check(order.Count == 1 && engine.ManualQueuePositions().Count == 0, "bulk removal never starts pending downloads");
        }
        finally
        {
            release.TrySetResult();
            foreach (var job in jobs) engine.CancelJob(job.Id);
        }
    }

    private static async Task Until(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Manual queue did not finish");
            await Task.Delay(20);
        }
    }
}
