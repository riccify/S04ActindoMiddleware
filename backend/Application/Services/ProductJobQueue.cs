using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Application.Services;

public enum ProductSyncJobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

public sealed class ProductJobLogEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string Endpoint { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string? Error { get; init; }
    public string? RequestPayload { get; init; }
    public string? ResponsePayload { get; init; }
}

public sealed class ProductJobInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Sku { get; init; } = string.Empty;
    public string Operation { get; init; } = string.Empty; // "create", "save", "full"
    public string? BufferId { get; init; }
    public ProductSyncJobStatus Status { get; set; } = ProductSyncJobStatus.Queued;
    public DateTimeOffset QueuedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Error { get; set; }
    public string? NavRequestPayload { get; set; }
    public string? NavResponsePayload { get; set; }

    private readonly List<ProductJobLogEntry> _logs = new();
    private readonly object _logsLock = new();

    public IReadOnlyList<ProductJobLogEntry> GetLogs()
    {
        lock (_logsLock) { return _logs.ToList(); }
    }

    internal void AddLogEntry(ProductJobLogEntry entry)
    {
        lock (_logsLock) { _logs.Add(entry); }
    }
}

public sealed class ProductJobQueue
{
    private static readonly AsyncLocal<Guid?> _currentJobId = new();

    /// <summary>Gibt die Job-ID des aktuell laufenden Jobs im aktuellen async-Kontext zurück.</summary>
    public static Guid? CurrentJobId => _currentJobId.Value;

    private readonly SemaphoreSlim _semaphore = new(5, 5);
    private readonly ConcurrentDictionary<Guid, ProductJobInfo> _jobs = new();
    private readonly ISqliteJobStore _store;
    private readonly ILogger<ProductJobQueue> _logger;

    public ProductJobQueue(ISqliteJobStore store, ILogger<ProductJobQueue> logger)
    {
        _store = store;
        _logger = logger;
        LoadFromStore();
    }

    private void LoadFromStore()
    {
        try
        {
            var persisted = _store.LoadAll();
            foreach (var record in persisted)
            {
                _jobs[record.Job.Id] = record.Job;
                foreach (var log in record.Logs)
                    record.Job.AddLogEntry(log);

                // Re-schedule expiry for completed jobs
                if (record.Job.Status == ProductSyncJobStatus.Completed && record.Job.CompletedAt.HasValue)
                {
                    var remaining = record.Job.CompletedAt.Value.AddDays(5) - DateTimeOffset.UtcNow;
                    if (remaining > TimeSpan.Zero)
                        _ = Task.Delay(remaining).ContinueWith(t =>
                        {
                            _store.Delete(record.Job.Id);
                            _jobs.TryRemove(record.Job.Id, out _);
                        });
                    else
                        _jobs.TryRemove(record.Job.Id, out _);
                }
            }
            _logger.LogInformation("Loaded {Count} persisted jobs from store", persisted.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load persisted jobs");
        }
    }

    /// <summary>Hängt einen API-Log-Eintrag an den Job mit der gegebenen ID.</summary>
    public void AddLog(Guid jobId, string endpoint, bool success, string? error = null, string? requestPayload = null, string? responsePayload = null)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
            return;

        var entry = new ProductJobLogEntry
        {
            Endpoint        = endpoint,
            Success         = success,
            Error           = error,
            RequestPayload  = requestPayload,
            ResponsePayload = responsePayload
        };
        job.AddLogEntry(entry);
        _store.AppendLog(jobId, entry);
    }

    /// <summary>Gibt die Log-Einträge eines Jobs zurück, oder null wenn der Job nicht existiert.</summary>
    public IReadOnlyList<ProductJobLogEntry>? GetLogs(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job.GetLogs() : null;

    /// <summary>
    /// Registriert einen synchronen Job direkt als laufend (kein async-Queue).
    /// Setzt den Job-Kontext für API-Logging. Der Aufrufer muss danach <see cref="CompleteSyncJob"/> aufrufen.
    /// </summary>
    public void RegisterSyncJob(Guid id, string sku, string operation, string? navRequestPayload = null)
    {
        var info = new ProductJobInfo
        {
            Id                = id,
            Sku               = sku,
            Operation         = operation,
            Status            = ProductSyncJobStatus.Running,
            StartedAt         = DateTimeOffset.UtcNow,
            NavRequestPayload = navRequestPayload
        };
        _jobs[info.Id] = info;
        _currentJobId.Value = info.Id;
        _store.Upsert(info);
        _logger.LogInformation(
            "Sync job registered: {JobId} SKU={Sku} Operation={Operation}",
            info.Id, sku, operation);
    }

    /// <summary>Gibt den Job mit der angegebenen ID zurück, oder null wenn nicht vorhanden.</summary>
    public ProductJobInfo? Get(Guid jobId) => _jobs.TryGetValue(jobId, out var job) ? job : null;

    /// <summary>Markiert einen synchronen Job als abgeschlossen oder fehlgeschlagen.</summary>
    public void CompleteSyncJob(Guid jobId, bool success, string? error = null)
    {
        _currentJobId.Value = null;
        if (!_jobs.TryGetValue(jobId, out var info))
            return;

        info.Status      = success ? ProductSyncJobStatus.Completed : ProductSyncJobStatus.Failed;
        info.CompletedAt = DateTimeOffset.UtcNow;
        info.Error       = error;
        _store.Upsert(info);

        _logger.LogInformation(
            "Sync job {Status}: {JobId} SKU={Sku}",
            info.Status, jobId, info.Sku);

        if (success)
            _ = Task.Delay(TimeSpan.FromDays(5))
                    .ContinueWith(t =>
                    {
                        _store.Delete(jobId);
                        _jobs.TryRemove(jobId, out _);
                    });
    }

    public Guid Enqueue(string sku, string operation, string? bufferId, Func<CancellationToken, Task> work, string? navRequestPayload = null)
    {
        var info = new ProductJobInfo
        {
            Sku               = sku,
            Operation         = operation,
            BufferId          = bufferId,
            NavRequestPayload = navRequestPayload
        };

        _jobs[info.Id] = info;
        _store.Upsert(info);

        _logger.LogInformation(
            "Product sync job queued: {JobId} SKU={Sku} Operation={Operation} BufferId={BufferId}",
            info.Id, sku, operation, bufferId ?? "(none)");

        _ = RunAsync(info, work);

        return info.Id;
    }

    private async Task RunAsync(ProductJobInfo info, Func<CancellationToken, Task> work)
    {
        await _semaphore.WaitAsync();

        info.Status    = ProductSyncJobStatus.Running;
        info.StartedAt = DateTimeOffset.UtcNow;
        _currentJobId.Value = info.Id;
        _store.Upsert(info);

        _logger.LogInformation(
            "Product sync job started: {JobId} SKU={Sku} Operation={Operation}",
            info.Id, info.Sku, info.Operation);

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            await work(cts.Token);
            info.Status = ProductSyncJobStatus.Completed;

            _logger.LogInformation(
                "Product sync job completed: {JobId} SKU={Sku}",
                info.Id, info.Sku);
        }
        catch (Exception ex)
        {
            info.Status = ProductSyncJobStatus.Failed;
            info.Error  = ex.Message;

            _logger.LogError(ex,
                "Product sync job failed: {JobId} SKU={Sku}",
                info.Id, info.Sku);
        }
        finally
        {
            _currentJobId.Value  = null;
            _semaphore.Release();
            info.CompletedAt = DateTimeOffset.UtcNow;
            _store.Upsert(info);

            // Successful jobs: keep 5 days; failed jobs: keep indefinitely
            if (info.Status == ProductSyncJobStatus.Completed)
                _ = Task.Delay(TimeSpan.FromDays(5))
                        .ContinueWith(t =>
                        {
                            _store.Delete(info.Id);
                            _jobs.TryRemove(info.Id, out _);
                        });
        }
    }

    public bool RemoveJob(Guid jobId)
    {
        _store.Delete(jobId);
        return _jobs.TryRemove(jobId, out _);
    }

    public int RemoveSuccessfulJobs()
    {
        var successfulIds = _jobs.Values
            .Where(job => job.Status == ProductSyncJobStatus.Completed)
            .Select(job => job.Id)
            .ToList();

        foreach (var jobId in successfulIds)
        {
            _store.Delete(jobId);
            _jobs.TryRemove(jobId, out _);
        }

        return successfulIds.Count;
    }

    public IReadOnlyList<ProductJobInfo> GetAll() =>
        _jobs.Values.OrderBy(j => j.QueuedAt).ToList();
}
