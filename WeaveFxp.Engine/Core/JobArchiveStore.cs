using Microsoft.Data.Sqlite;
using WeaveFxp.Engine.Models;

namespace WeaveFxp.Engine.Core;

// Append-friendly SQLite storage for terminal job history and the bounded runtime log.
// Summary columns keep History cheap; payload_json preserves every event/file row.
internal sealed class JobArchiveStore
{
    private readonly string _connectionString;

    public JobArchiveStore(string path)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString();

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS archived_jobs (
                id TEXT PRIMARY KEY,
                batch_id TEXT NOT NULL,
                type INTEGER NOT NULL,
                state INTEGER NOT NULL,
                created_ticks INTEGER NOT NULL,
                started_ticks INTEGER NOT NULL,
                finished_ticks INTEGER NOT NULL,
                from_site TEXT NOT NULL,
                to_site TEXT NOT NULL,
                source_path TEXT NOT NULL,
                dest_path TEXT NOT NULL,
                label TEXT NOT NULL,
                error TEXT NOT NULL,
                files_done INTEGER NOT NULL,
                files_total INTEGER NOT NULL,
                bytes_done INTEGER NOT NULL,
                bytes_total INTEGER NOT NULL,
                cumulative_bytes INTEGER NOT NULL,
                payload_json TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_archived_jobs_created
                ON archived_jobs(created_ticks DESC);
            CREATE INDEX IF NOT EXISTS ix_archived_jobs_state
                ON archived_jobs(state, created_ticks DESC);
            CREATE TABLE IF NOT EXISTS runtime_logs (
                seq INTEGER PRIMARY KEY,
                time_ticks INTEGER NOT NULL,
                category TEXT NOT NULL,
                site TEXT NOT NULL,
                level TEXT NOT NULL,
                message TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_runtime_logs_time
                ON runtime_logs(time_ticks DESC);
            """;
        command.ExecuteNonQuery();
    }

    public void AppendLogs(IReadOnlyCollection<LogEntry> entries, int maxEntries)
    {
        if (entries.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO runtime_logs (seq, time_ticks, category, site, level, message)
                VALUES ($seq, $time, $category, $site, $level, $message)
                ON CONFLICT(seq) DO UPDATE SET
                    time_ticks=excluded.time_ticks, category=excluded.category,
                    site=excluded.site, level=excluded.level, message=excluded.message;
                """;
            command.Parameters.AddWithValue("$seq", entry.Seq);
            command.Parameters.AddWithValue("$time", Ticks(entry.Time));
            command.Parameters.AddWithValue("$category", entry.Category ?? "");
            command.Parameters.AddWithValue("$site", entry.Site ?? "");
            command.Parameters.AddWithValue("$level", entry.Level ?? "");
            command.Parameters.AddWithValue("$message", entry.Message ?? "");
            command.ExecuteNonQuery();
        }
        transaction.Commit();

        using var prune = connection.CreateCommand();
        prune.CommandText = """
            DELETE FROM runtime_logs
            WHERE seq IN (
                SELECT seq FROM runtime_logs
                ORDER BY seq DESC
                LIMIT -1 OFFSET $max
            );
            """;
        prune.Parameters.AddWithValue("$max", Math.Clamp(maxEntries, 100, 100000));
        prune.ExecuteNonQuery();
    }

    public List<LogEntry> RecentLogs(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT seq, time_ticks, category, site, level, message
            FROM (
                SELECT seq, time_ticks, category, site, level, message
                FROM runtime_logs
                ORDER BY seq DESC
                LIMIT $limit
            )
            ORDER BY seq ASC;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = command.ExecuteReader();
        var result = new List<LogEntry>();
        while (reader.Read())
        {
            result.Add(new LogEntry
            {
                Seq = reader.GetInt64(0),
                Time = FromTicks(reader.GetInt64(1)),
                Category = reader.GetString(2),
                Site = reader.GetString(3),
                Level = reader.GetString(4),
                Message = reader.GetString(5),
            });
        }
        return result;
    }

    public int ClearLogs()
    {
        using var connection = Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM runtime_logs;";
        var removed = Convert.ToInt32(count.ExecuteScalar());
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM runtime_logs;";
        command.ExecuteNonQuery();
        return removed;
    }

    public void Archive(IReadOnlyCollection<(Job Job, string Json)> jobs)
    {
        if (jobs.Count == 0) return;
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        foreach (var item in jobs)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO archived_jobs (
                    id, batch_id, type, state, created_ticks, started_ticks, finished_ticks,
                    from_site, to_site, source_path, dest_path, label, error,
                    files_done, files_total, bytes_done, bytes_total, cumulative_bytes, payload_json)
                VALUES (
                    $id, $batch, $type, $state, $created, $started, $finished,
                    $from, $to, $source, $dest, $label, $error,
                    $filesDone, $filesTotal, $bytesDone, $bytesTotal, $cumulative, $json)
                ON CONFLICT(id) DO UPDATE SET
                    batch_id=excluded.batch_id, type=excluded.type, state=excluded.state,
                    created_ticks=excluded.created_ticks, started_ticks=excluded.started_ticks,
                    finished_ticks=excluded.finished_ticks, from_site=excluded.from_site,
                    to_site=excluded.to_site, source_path=excluded.source_path,
                    dest_path=excluded.dest_path, label=excluded.label, error=excluded.error,
                    files_done=excluded.files_done, files_total=excluded.files_total,
                    bytes_done=excluded.bytes_done, bytes_total=excluded.bytes_total,
                    cumulative_bytes=excluded.cumulative_bytes, payload_json=excluded.payload_json;
                """;
            AddJobParameters(command, item.Job, item.Json);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public List<Job> Headers(int limit)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, batch_id, type, state, created_ticks, started_ticks, finished_ticks,
                   from_site, to_site, source_path, dest_path, label, error,
                   files_done, files_total, bytes_done, bytes_total, cumulative_bytes
            FROM archived_jobs
            ORDER BY created_ticks DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100000));
        using var reader = command.ExecuteReader();
        var result = new List<Job>();
        while (reader.Read())
        {
            result.Add(new Job
            {
                Id = reader.GetString(0),
                BatchId = reader.GetString(1),
                Type = (JobType)reader.GetInt32(2),
                State = (JobState)reader.GetInt32(3),
                CreatedAt = FromTicks(reader.GetInt64(4)),
                StartedAt = FromTicks(reader.GetInt64(5)),
                FinishedAt = FromTicks(reader.GetInt64(6)),
                Request = new TransferRequest
                {
                    FromSite = reader.GetString(7),
                    ToSite = reader.GetString(8),
                    SourcePath = reader.GetString(9),
                    DestPath = reader.GetString(10),
                    Label = reader.GetString(11),
                    Race = (JobType)reader.GetInt32(2) == JobType.Race,
                },
                Error = reader.GetString(12),
                FilesDone = reader.GetInt32(13),
                FilesTotal = reader.GetInt32(14),
                BytesDone = reader.GetInt64(15),
                BytesTotal = reader.GetInt64(16),
                CumulativeBytes = reader.GetInt64(17),
            });
        }
        return result;
    }

    public string? Payload(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload_json FROM archived_jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() as string;
    }

    public int Count()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM archived_jobs;";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public bool Delete(string id)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM archived_jobs WHERE id=$id;";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteNonQuery() > 0;
    }

    public int Clear()
    {
        using var connection = Open();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM archived_jobs;";
        var removed = Convert.ToInt32(count.ExecuteScalar());
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM archived_jobs; PRAGMA wal_checkpoint(TRUNCATE); VACUUM;";
        command.ExecuteNonQuery();
        return removed;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void AddJobParameters(SqliteCommand command, Job job, string json)
    {
        command.Parameters.AddWithValue("$id", job.Id);
        command.Parameters.AddWithValue("$batch", job.BatchId ?? "");
        command.Parameters.AddWithValue("$type", (int)job.Type);
        command.Parameters.AddWithValue("$state", (int)job.State);
        command.Parameters.AddWithValue("$created", Ticks(job.CreatedAt));
        command.Parameters.AddWithValue("$started", Ticks(job.StartedAt));
        command.Parameters.AddWithValue("$finished", Ticks(job.FinishedAt));
        command.Parameters.AddWithValue("$from", job.Request.FromSite ?? "");
        command.Parameters.AddWithValue("$to", job.Request.ToSite ?? "");
        command.Parameters.AddWithValue("$source", job.Request.SourcePath ?? "");
        command.Parameters.AddWithValue("$dest", job.Request.DestPath ?? "");
        command.Parameters.AddWithValue("$label", job.Request.Label ?? "");
        command.Parameters.AddWithValue("$error", job.Error ?? "");
        command.Parameters.AddWithValue("$filesDone", job.FilesDone);
        command.Parameters.AddWithValue("$filesTotal", job.FilesTotal);
        command.Parameters.AddWithValue("$bytesDone", job.BytesDone);
        command.Parameters.AddWithValue("$bytesTotal", job.BytesTotal);
        command.Parameters.AddWithValue("$cumulative", job.CumulativeBytes);
        command.Parameters.AddWithValue("$json", json);
    }

    private static long Ticks(DateTime value) => value == default ? 0 : value.ToUniversalTime().Ticks;
    private static DateTime FromTicks(long ticks) => ticks <= 0 ? default : new DateTime(ticks, DateTimeKind.Utc);
}
