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
    private readonly ILogger<ProductJobQueue> _logger;

    public ProductJobQueue(ILogger<ProductJobQueue> logger)
    {
        _logger = logger;
    }

    /// <summary>Hängt einen API-Log-Eintrag an den Job mit der gegebenen ID.</summary>
    public void AddLog(Guid jobId, string endpoint, bool success, string? error = null, string? requestPayload = null, string? responsePayload = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
            job.AddLogEntry(new ProductJobLogEntry
            {
                Endpoint = endpoint,
                Success = success,
                Error = error,
                RequestPayload = requestPayload,
                ResponsePayload = responsePayload
            });
    }

    /// <summary>Gibt die Log-Einträge eines Jobs zurück, oder null wenn der Job nicht existiert.</summary>
    public IReadOnlyList<ProductJobLogEntry>? GetLogs(Guid jobId) =>
        _jobs.TryGetValue(jobId, out var job) ? job.GetLogs() : null;

    /// <summary>
    /// Registriert einen synchronen Job direkt als laufend (kein async-Queue).
    /// Setzt den Job-Kontext für API-Logging. Der Aufrufer muss danach <see cref="CompleteSyncJob"/> aufrufen.
    /// </summary>
    public void RegisterSyncJob(Guid id, string sku, string operation)
    {
        var info = new ProductJobInfo
        {
            Id = id,
            Sku = sku,
            Operation = operation,
            Status = ProductSyncJobStatus.Running,
            StartedAt = DateTimeOffset.UtcNow
        };
        _jobs[info.Id] = info;
        _currentJobId.Value = info.Id;
        _logger.LogInformation(
            "Sync job registered: {JobId} SKU={Sku} Operation={Operation}",
            info.Id, sku, operation);
    }

    /// <summary>Markiert einen synchronen Job als abgeschlossen oder fehlgeschlagen.</summary>
    public void CompleteSyncJob(Guid jobId, bool success, string? error = null)
    {
        _currentJobId.Value = null;
        if (!_jobs.TryGetValue(jobId, out var info))
            return;

        info.Status = success ? ProductSyncJobStatus.Completed : ProductSyncJobStatus.Failed;
        info.CompletedAt = DateTimeOffset.UtcNow;
        info.Error = error;

        _logger.LogInformation(
            "Sync job {Status}: {JobId} SKU={Sku}",
            info.Status, jobId, info.Sku);

        if (success)
            _ = Task.Delay(TimeSpan.FromDays(5))
                    .ContinueWith(t => _jobs.TryRemove(jobId, out _));
    }

    public Guid Enqueue(string sku, string operation, string? bufferId, Func<CancellationToken, Task> work)
    {
        var info = new ProductJobInfo
        {
            Sku = sku,
            Operation = operation,
            BufferId = bufferId
        };

        _jobs[info.Id] = info;

        _logger.LogInformation(
            "Product sync job queued: {JobId} SKU={Sku} Operation={Operation} BufferId={BufferId}",
            info.Id, sku, operation, bufferId ?? "(none)");

        _ = RunAsync(info, work);

        return info.Id;
    }

    private async Task RunAsync(ProductJobInfo info, Func<CancellationToken, Task> work)
    {
        await _semaphore.WaitAsync();

        info.Status = ProductSyncJobStatus.Running;
        info.StartedAt = DateTimeOffset.UtcNow;
        _currentJobId.Value = info.Id;

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
            info.Error = ex.Message;

            _logger.LogError(ex,
                "Product sync job failed: {JobId} SKU={Sku}",
                info.Id, info.Sku);
        }
        finally
        {
            _currentJobId.Value = null;
            _semaphore.Release();
            info.CompletedAt = DateTimeOffset.UtcNow;

            // Successful jobs: keep 5 days; failed jobs: keep indefinitely
            if (info.Status == ProductSyncJobStatus.Completed)
                _ = Task.Delay(TimeSpan.FromDays(5))
                        .ContinueWith(t => _jobs.TryRemove(info.Id, out _));
        }
    }

    public IReadOnlyList<ProductJobInfo> GetAll() =>
        _jobs.Values.OrderBy(j => j.QueuedAt).ToList();
}
