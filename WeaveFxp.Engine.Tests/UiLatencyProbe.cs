using System.Diagnostics;
using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class UiLatencyProbe
{
    public static async Task Run()
    {
        var path = Path.Combine(Path.GetTempPath(), "weave-ui-probe-" + Guid.NewGuid().ToString("N"), "state.json");
        var store = new JsonStore(path);
        for (var i = 0; i < 35; i++)
        {
            store.UpsertJob(new Job
            {
                Id = i.ToString(), Type = JobType.Race, State = JobState.Queued, CreatedAt = DateTime.UtcNow,
                Request = new() { Race = true, FromSite = "source", ToSite = "target", SourcePath = "/release", DryRun = true },
                Events = Enumerable.Range(0, 1000).Select(n => new JobEvent { Message = "poll: waiting for queued file " + n }).ToList(),
                Files = Enumerable.Range(0, 2500).Select(n => new FileTransfer { Name = "payload.r" + n, Status = "fail", Size = 100000000, Error = "temporary rejection" }).ToList()
            });
        }
        var timer = Stopwatch.StartNew();
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var jobs = store.Jobs();
        var cold = timer.Elapsed.TotalMilliseconds;
        var cloneBytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        timer.Restart();
        store.UpdateJobTransient("0", j => j.SpeedBps = 1234);
        var update = timer.Elapsed.TotalMilliseconds;
        var samples = new List<double>();
        for (var i = 0; i < 3; i++)
        {
            await Task.Delay(300);
            timer.Restart();
            store.Jobs();
            samples.Add(timer.Elapsed.TotalMilliseconds);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { jobs = jobs.Count, filesPerJob = 2500, eventsPerJob = 1000, coldJobsMs = cold, periodicJobsMs = samples, coldAllocatedMB = cloneBytes / 1e6, transientUpdateMs = update }));
    }
}
