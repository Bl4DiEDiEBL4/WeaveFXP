using System.Text.Json;
using WeaveFxp.Engine.Core;
using WeaveFxp.Engine.Models;

internal static class HistoryRetentionTests
{
    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "weavefxp-history-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception("FAIL: " + name);
            Console.WriteLine("PASS: " + name);
        }
        Job Job(int n, JobState state = JobState.Succeeded) => new()
        {
            Id = n.ToString("D5"), State = state,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(n),
            Events = new() { new() { Message = "retained details " + n } }
        };
        (Job Job, string Json) Payload(Job j) => (j, JsonSerializer.Serialize(j));
        Check(JsonSerializer.Deserialize<AppSettings>("{}")!.WithDefaults().CompletedJobHistoryLimit == 1000,
            "legacy settings receive a finite default of 1000");
        Check(new AppSettings { CompletedJobHistoryLimit = 0 }.WithDefaults().CompletedJobHistoryLimit == 1,
            "zero cannot enable unlimited history");
        Check(new AppSettings { CompletedJobHistoryLimit = int.MaxValue }.WithDefaults().CompletedJobHistoryLimit == 100000,
            "history limit is bounded");

        var archive = new JobArchiveStore(Path.Combine(root, "archive.db"));
        archive.Archive(Enumerable.Range(0, 40).Select(n => Payload(Job(n))).ToList());
        var hot = new[] { Job(39), Job(40, JobState.Failed), Job(41, JobState.Cancelled), Job(0, JobState.Running), Job(1, JobState.Queued) };
        var removed = archive.RetainNewest(5, hot);
        Check(removed.Count == 0 && archive.Count() == 2, "one limit counts hot and archived terminal jobs without duplicates");
        Check(archive.Headers(100).Select(j => j.Id).Order().SequenceEqual(new[] { "00037", "00038" }),
            "oldest archived jobs are deleted first");
        Check(archive.Payload("00038")!.Contains("retained details 38"), "retained payload and event details are preserved");
        removed = archive.RetainNewest(1, hot);
        Check(removed.Order().SequenceEqual(new[] { "00039", "00040" }) && archive.Count() == 0,
            "lower limit includes failed/cancelled jobs and protects running/queued jobs");
        archive.Archive(new[] { Payload(Job(50)), Payload(Job(51)) });
        archive.RetainNewest(1, Array.Empty<Job>());
        Check(new JobArchiveStore(Path.Combine(root, "archive.db")).Headers(100).Single().Id == "00051",
            "archive-only cleanup persists across reopening");

        var stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(stateDir);
        var oldArchive = new JobArchiveStore(Path.Combine(stateDir, "history.db"));
        oldArchive.Archive(Enumerable.Range(0, 40).Select(n => Payload(Job(n))).ToList());
        var state = new State
        {
            Settings = new AppSettings { CompletedJobHistoryLimit = 5 },
            Jobs = new[] { Job(40), Job(41, JobState.Failed), Job(42, JobState.Cancelled), Job(0, JobState.Running), Job(1, JobState.Queued) }
                .ToDictionary(j => j.Id)
        };
        var path = Path.Combine(stateDir, "state.json");
        File.WriteAllText(path, JsonSerializer.Serialize(state, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }));
        var store = new JsonStore(path);
        Check(store.HistoryJobs().Count(j => j.Terminal) == 5 && store.HistoryJobs().Count(j => !j.Terminal) == 2,
            "startup enforces the limit even below the hot JSON threshold");
        var settings = store.Settings();
        settings.CompletedJobHistoryLimit = 2;
        store.UpdateSettings(settings);
        Check(store.HistoryJobs().Where(j => j.Terminal).Select(j => j.Id).Order().SequenceEqual(new[] { "00041", "00042" }),
            "saving a lower limit immediately prunes both stores");
        store.UpdateJob("00000", j => j.State = JobState.Succeeded);
        Check(store.Job("00000") == null && store.HistoryJobs().Count(j => j.Terminal) == 2,
            "an old running job finishing cannot resurrect a pruned job");
        settings.CompletedJobHistoryLimit = 30;
        settings.StoredJobHistoryLimit = 25;
        store.UpdateSettings(settings);
        for (var n = 50; n < 100; n++) store.UpsertJob(Job(n));
        Check(store.HistoryJobs().Count(j => j.Terminal) == 30 && store.ArchivedJobCount() > 0,
            "new completions keep the total cap while spilling into SQLite");
        Check(store.Job("00001")?.State == JobState.Queued, "queued job survives repeated cleanup");
        Check(store.Job("00069") == null && store.Job("00070") != null && store.Job("00099") != null,
            "exactly the newest thirty jobs remain queryable");
        Check(store.LoadWarning == null || !store.LoadWarning.Contains("failed", StringComparison.OrdinalIgnoreCase),
            "cleanup completed without storage errors");
        Console.WriteLine("History retention tests passed. Isolated test data: " + root);
    }
}
