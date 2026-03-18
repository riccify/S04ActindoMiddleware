using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Application.Services;

public sealed record PersistedJob(ProductJobInfo Job, IReadOnlyList<ProductJobLogEntry> Logs);

public interface ISqliteJobStore
{
    IReadOnlyList<PersistedJob> LoadAll();
    void Upsert(ProductJobInfo job);
    void AppendLog(Guid jobId, ProductJobLogEntry entry);
    void Delete(Guid jobId);
}

public sealed class SqliteJobStore : ISqliteJobStore
{
    private const string DefaultConnectionString = "Data Source=App_Data/dashboard.db";
    private readonly string _connectionString;
    private readonly ILogger<SqliteJobStore> _logger;

    public SqliteJobStore(
        IConfiguration configuration,
        IHostEnvironment hostEnvironment,
        ILogger<SqliteJobStore> logger)
    {
        _logger = logger;
        _connectionString = BuildConnectionString(
            configuration.GetConnectionString("Dashboard"),
            hostEnvironment.ContentRootPath);
        EnsureTables();
    }

    private void EnsureTables()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GetDatabasePath())!);
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        Exec(connection, null,
            """
            CREATE TABLE IF NOT EXISTS JobQueue (
                Id          TEXT PRIMARY KEY NOT NULL,
                Sku         TEXT NOT NULL DEFAULT '',
                Operation   TEXT NOT NULL DEFAULT '',
                BufferId    TEXT NULL,
                Status      TEXT NOT NULL DEFAULT 'queued',
                QueuedAt    TEXT NOT NULL,
                StartedAt   TEXT NULL,
                CompletedAt TEXT NULL,
                Error       TEXT NULL
            )
            """);

        Exec(connection, null,
            """
            CREATE TABLE IF NOT EXISTS JobLogs (
                RowId           INTEGER PRIMARY KEY AUTOINCREMENT,
                JobId           TEXT NOT NULL,
                Timestamp       TEXT NOT NULL,
                Endpoint        TEXT NOT NULL DEFAULT '',
                Success         INTEGER NOT NULL DEFAULT 0,
                Error           TEXT NULL,
                RequestPayload  TEXT NULL,
                ResponsePayload TEXT NULL
            )
            """);

        Exec(connection, null,
            "CREATE INDEX IF NOT EXISTS IX_JobLogs_JobId ON JobLogs (JobId)");

        // Migrate: add NavRequestPayload / NavResponsePayload columns if they don't exist yet
        foreach (var col in new[] { "NavRequestPayload", "NavResponsePayload" })
        {
            try { Exec(connection, null, $"ALTER TABLE JobQueue ADD COLUMN {col} TEXT NULL"); }
            catch { /* column already exists */ }
        }
    }

    public IReadOnlyList<PersistedJob> LoadAll()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // Mark any jobs that were still running/queued when the app was shut down
        Exec(connection, null,
            """
            UPDATE JobQueue
            SET Status      = 'failed',
                CompletedAt = @now,
                Error       = 'Middleware neugestartet'
            WHERE Status IN ('running', 'queued')
            """,
            ("@now", DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

        var jobs = new Dictionary<Guid, (ProductJobInfo info, List<ProductJobLogEntry> logs)>();

        using var loadCmd = connection.CreateCommand();
        loadCmd.CommandText =
            """
            SELECT Id, Sku, Operation, BufferId, Status, QueuedAt, StartedAt, CompletedAt, Error, NavRequestPayload, NavResponsePayload
            FROM JobQueue
            WHERE Status = 'failed'
               OR (Status = 'completed' AND CompletedAt > @cutoff)
            ORDER BY QueuedAt ASC
            """;
        loadCmd.Parameters.AddWithValue("@cutoff",
            DateTimeOffset.UtcNow.AddDays(-5).ToString("o", CultureInfo.InvariantCulture));

        using (var reader = loadCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var id = Guid.Parse(reader.GetString(0));
                var status = reader.GetString(4) == "completed"
                    ? ProductSyncJobStatus.Completed
                    : ProductSyncJobStatus.Failed;

                var info = new ProductJobInfo
                {
                    Id                 = id,
                    Sku                = reader.GetString(1),
                    Operation          = reader.GetString(2),
                    BufferId           = reader.IsDBNull(3) ? null : reader.GetString(3),
                    QueuedAt           = ParseDate(reader.GetString(5)) ?? DateTimeOffset.UtcNow,
                    Status             = status,
                    StartedAt          = reader.IsDBNull(6) ? null : ParseDate(reader.GetString(6)),
                    CompletedAt        = reader.IsDBNull(7) ? null : ParseDate(reader.GetString(7)),
                    Error              = reader.IsDBNull(8) ? null : reader.GetString(8),
                    NavRequestPayload  = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null,
                    NavResponsePayload = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
                };

                jobs[id] = (info, new List<ProductJobLogEntry>());
            }
        }

        if (jobs.Count == 0)
            return Array.Empty<PersistedJob>();

        // Load log entries for all loaded jobs
        var idList = string.Join(",", jobs.Keys.Select(id => $"'{id}'"));
        using var logsCmd = connection.CreateCommand();
        logsCmd.CommandText =
            $"""
            SELECT JobId, Timestamp, Endpoint, Success, Error, RequestPayload, ResponsePayload
            FROM JobLogs
            WHERE JobId IN ({idList})
            ORDER BY RowId ASC
            """;

        using (var reader = logsCmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var jobId = Guid.Parse(reader.GetString(0));
                if (!jobs.TryGetValue(jobId, out var tuple))
                    continue;

                tuple.logs.Add(new ProductJobLogEntry
                {
                    Timestamp       = ParseDate(reader.GetString(1)) ?? DateTimeOffset.UtcNow,
                    Endpoint        = reader.GetString(2),
                    Success         = reader.GetInt32(3) == 1,
                    Error           = reader.IsDBNull(4) ? null : reader.GetString(4),
                    RequestPayload  = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ResponsePayload = reader.IsDBNull(6) ? null : reader.GetString(6),
                });
            }
        }

        return jobs.Values.Select(t => new PersistedJob(t.info, t.logs)).ToList();
    }

    public void Upsert(ProductJobInfo job)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            Exec(connection, null,
                """
                INSERT INTO JobQueue (Id, Sku, Operation, BufferId, Status, QueuedAt, StartedAt, CompletedAt, Error, NavRequestPayload, NavResponsePayload)
                VALUES (@id, @sku, @operation, @bufferId, @status, @queuedAt, @startedAt, @completedAt, @error, @navRequest, @navResponse)
                ON CONFLICT(Id) DO UPDATE SET
                    Status             = excluded.Status,
                    StartedAt          = excluded.StartedAt,
                    CompletedAt        = excluded.CompletedAt,
                    Error              = excluded.Error,
                    NavRequestPayload  = COALESCE(excluded.NavRequestPayload, JobQueue.NavRequestPayload),
                    NavResponsePayload = COALESCE(excluded.NavResponsePayload, JobQueue.NavResponsePayload)
                """,
                ("@id",          job.Id.ToString()),
                ("@sku",         job.Sku),
                ("@operation",   job.Operation),
                ("@bufferId",    (object?)job.BufferId ?? DBNull.Value),
                ("@status",      StatusStr(job.Status)),
                ("@queuedAt",    job.QueuedAt.ToString("o", CultureInfo.InvariantCulture)),
                ("@startedAt",   job.StartedAt.HasValue ? job.StartedAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value),
                ("@completedAt", job.CompletedAt.HasValue ? job.CompletedAt.Value.ToString("o", CultureInfo.InvariantCulture) : (object)DBNull.Value),
                ("@error",       (object?)job.Error ?? DBNull.Value),
                ("@navRequest",  (object?)job.NavRequestPayload ?? DBNull.Value),
                ("@navResponse", (object?)job.NavResponsePayload ?? DBNull.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert job {JobId}", job.Id);
        }
    }

    public void AppendLog(Guid jobId, ProductJobLogEntry entry)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            Exec(connection, null,
                """
                INSERT INTO JobLogs (JobId, Timestamp, Endpoint, Success, Error, RequestPayload, ResponsePayload)
                VALUES (@jobId, @timestamp, @endpoint, @success, @error, @requestPayload, @responsePayload)
                """,
                ("@jobId",          jobId.ToString()),
                ("@timestamp",      entry.Timestamp.ToString("o", CultureInfo.InvariantCulture)),
                ("@endpoint",       entry.Endpoint),
                ("@success",        entry.Success ? 1 : 0),
                ("@error",          (object?)entry.Error ?? DBNull.Value),
                ("@requestPayload", (object?)entry.RequestPayload ?? DBNull.Value),
                ("@responsePayload",(object?)entry.ResponsePayload ?? DBNull.Value));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to append log for job {JobId}", jobId);
        }
    }

    public void Delete(Guid jobId)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var tx = connection.BeginTransaction();
            Exec(connection, tx, "DELETE FROM JobLogs WHERE JobId = @id", ("@id", jobId.ToString()));
            Exec(connection, tx, "DELETE FROM JobQueue WHERE Id = @id",   ("@id", jobId.ToString()));
            tx.Commit();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete job {JobId}", jobId);
        }
    }

    // --- helpers ---

    private static void Exec(SqliteConnection connection, SqliteTransaction? tx, string sql,
        params (string name, object value)[] parameters)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static string StatusStr(ProductSyncJobStatus status) => status switch
    {
        ProductSyncJobStatus.Queued    => "queued",
        ProductSyncJobStatus.Running   => "running",
        ProductSyncJobStatus.Completed => "completed",
        _                              => "failed"
    };

    private static DateTimeOffset? ParseDate(string? value) =>
        !string.IsNullOrEmpty(value) &&
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
            ? d : null;

    private string GetDatabasePath()
    {
        var builder = new SqliteConnectionStringBuilder(_connectionString);
        return builder.DataSource;
    }

    private static string BuildConnectionString(string? configured, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(
            string.IsNullOrWhiteSpace(configured) ? DefaultConnectionString : configured);
        if (!Path.IsPathRooted(builder.DataSource))
            builder.DataSource = Path.Combine(contentRoot, builder.DataSource);
        return builder.ToString();
    }
}
