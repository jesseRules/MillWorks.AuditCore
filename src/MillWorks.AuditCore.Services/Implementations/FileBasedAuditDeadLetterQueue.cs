using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;

/// <summary>
/// File-based implementation of Dead Letter Queue.
///
/// <para><b>Operational boundaries:</b></para>
/// <list type="bullet">
///   <item>Designed exclusively for small-volume, single-instance deployments.</item>
///   <item>Uses in-process <see cref="SemaphoreSlim"/> locking — not safe across multiple processes.</item>
///   <item>Listing, statistics, and purge operations scan the directory — O(n) in queue size.</item>
///   <item>Soft warning threshold: <see cref="ResilienceOptions.FileBasedMaxQueueSize"/> (default 1000).</item>
///   <item>Hard capacity cap: <see cref="ResilienceOptions.FileBasedHardCapacity"/> (default 5000).
///     New events are rejected when this limit is reached.</item>
///   <item>Processed files are retained in a <c>Processed/</c> subfolder for
///     <see cref="ResilienceOptions.ProcessedRetention"/> (default 7 days) before cleanup.</item>
/// </list>
///
/// <para>For high-volume or multi-instance scenarios, use <see cref="RedisAuditDeadLetterQueue"/>.</para>
/// </summary>
public sealed class FileBasedAuditDeadLetterQueue : IAuditDeadLetterQueue
{
    /// <summary>
    /// Dead letter queue storage path
    /// </summary>
    private readonly string _deadLetterPath;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<FileBasedAuditDeadLetterQueue> _logger;

    /// <summary>
    /// Configuration instance
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// File lock for thread safety
    /// </summary>
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <summary>
    /// JSON serializer options
    /// </summary>
    private readonly JsonSerializerOptions _jsonOptions;

    /// <summary>
    /// Service scope factory for creating scopes
    /// </summary>
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Field redactor for sanitizing sensitive data before DLQ storage
    /// </summary>
    private readonly IAuditFieldRedactor _fieldRedactor;

    /// <summary>
    /// Whether to include full stack traces in DLQ entries
    /// </summary>
    private readonly bool _includeStackTraces;

    /// <summary>
    /// How long processed files are retained before deletion
    /// </summary>
    private readonly TimeSpan _processedRetention;

    /// <summary>
    /// Maximum queue size before warnings are logged
    /// </summary>
    private readonly int _maxQueueSize;

    /// <summary>
    /// Hard capacity limit. New events are rejected when queue reaches this size.
    /// 0 disables the hard cap (warning-only behavior).
    /// </summary>
    private readonly int _hardCapacity;

    /// <summary>
    /// File-based Audit Dead Letter Queue constructor
    /// </summary>
    public FileBasedAuditDeadLetterQueue(
        ILogger<FileBasedAuditDeadLetterQueue> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider,
        IAuditFieldRedactor fieldRedactor,
        ResilienceOptions? resilienceOptions = null)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
        _fieldRedactor = fieldRedactor;
        _includeStackTraces = resilienceOptions?.IncludeStackTraces ?? false;
        _processedRetention = resilienceOptions?.ProcessedRetention ?? TimeSpan.FromDays(7);
        _maxQueueSize = resilienceOptions?.FileBasedMaxQueueSize ?? 1000;
        _hardCapacity = resilienceOptions?.FileBasedHardCapacity ?? 5000;

        _deadLetterPath = configuration["Audit:DeadLetterQueue:Path"]
                          ?? Path.Combine(Path.GetTempPath(), "MillWorks.Audit", "AuditDLQ");

        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        EnsureDirectoryExists();
    }

    /// <summary>
    /// Store a failed audit event in the dead letter queue
    /// </summary>
    public async Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        var redactedEvent = AuditEventRedactionHelper.RedactEvent(_fieldRedactor, auditEvent);

        await _fileLock.WaitAsync();
        try
        {
            var deadLetterEvent = new DeadLetterAuditEvent
            {
                OriginalEvent = redactedEvent,
                FailureReason = reason ?? "Unknown",
                ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
                ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
                ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces),
                Metadata = new Dictionary<string, object>
                {
                    ["EventType"] = auditEvent.EventType,
                    ["EventId"] = auditEvent.EventId.ToString(),
                    ["StartDate"] = auditEvent.StartDate
                }
            };

            WarnIfQueueFull();
            await SaveDeadLetterEventAsync(deadLetterEvent);

            _logger.LogWarning("Audit event {EventId} stored in dead letter queue. Reason: {Reason}",
                auditEvent.EventId, reason);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Store a failed audit entity in the dead letter queue
    /// </summary>
    public async Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null,
        string? reason = null)
    {
        await _fileLock.WaitAsync();
        try
        {
            var deadLetterEvent = new DeadLetterAuditEvent
            {
                OriginalEntity = entity,
                FailureReason = reason ?? "Unknown",
                ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
                ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
                ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces),
                Metadata = new Dictionary<string, object>
                {
                    ["EventType"] = entity.EventType ?? "Unknown",
                    ["EventId"] = entity.EventId.ToString(),
                    ["InsertedDate"] = entity.InsertedDate ?? DateTimeOffset.MinValue
                }
            };

            WarnIfQueueFull();
            await SaveDeadLetterEventAsync(deadLetterEvent);

            _logger.LogWarning("Audit entity {EventId} stored in dead letter queue. Reason: {Reason}",
                entity.EventId, reason);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Get a list of failed events from the dead letter queue
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
    {
        await _fileLock.WaitAsync();
        try
        {
            return await ReadFailedEventsInternalAsync(maxCount);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Internal method that reads failed events without acquiring the lock.
    /// Callers must already hold _fileLock.
    /// </summary>
    private async Task<List<DeadLetterAuditEvent>> ReadFailedEventsInternalAsync(int maxCount)
    {
        var events = new List<DeadLetterAuditEvent>();
        var files = Directory.GetFiles(_deadLetterPath, "dlq_*.json");

        // Sort by creation time descending, then only read the files we need
        Array.Sort(files, (a, b) => File.GetCreationTimeUtc(b).CompareTo(File.GetCreationTimeUtc(a)));
        var filesToRead = files.Length <= maxCount ? files : files[..maxCount];

        foreach (var file in filesToRead)
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);
                if (evt != null)
                {
                    events.Add(evt);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read dead letter file {FileName}", Path.GetFileName(file));
            }
        }

        return events;
    }

    /// <summary>
    /// Get failed events within a specific date range
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate,
        DateTimeOffset endDate)
    {
        await _fileLock.WaitAsync();
        try
        {
            var allEvents = await ReadFailedEventsInternalAsync(int.MaxValue);
            return allEvents
                .Where(e => e.FailedAt >= startDate && e.FailedAt <= endDate)
                .ToList();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Reprocess a specific dead letter event by its ID
    /// </summary>
    public async Task<bool> ReprocessEventAsync(string deadLetterId)
    {
        await _fileLock.WaitAsync();
        try
        {
            var filePath = GetFilePathForEvent(deadLetterId);
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Dead letter event {Id} not found", deadLetterId);
                return false;
            }

            var json = await File.ReadAllTextAsync(filePath);
            var deadLetterEvent = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);

            if (deadLetterEvent == null)
            {
                _logger.LogError("Failed to deserialize dead letter event {Id}", deadLetterId);
                return false;
            }

            // Update retry information
            deadLetterEvent.RetryCount++;
            deadLetterEvent.LastRetryAt = DateTimeOffset.UtcNow;

            try
            {
                // Create a scope to get the scoped IAuditLogger
                using var scope = _serviceScopeFactory.CreateScope();
                var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();

                // Attempt to reprocess
                if (auditLogger != null && deadLetterEvent.OriginalEvent != null)
                {
                    await auditLogger.LogAsync(deadLetterEvent.OriginalEvent);

                    // Mark as processed
                    deadLetterEvent.IsProcessed = true;
                    deadLetterEvent.ProcessedAt = DateTimeOffset.UtcNow;

                    // Update the file
                    await SaveDeadLetterEventAsync(deadLetterEvent);

                    _logger.LogInformation("Successfully reprocessed dead letter event {Id}", deadLetterId);
                    return true;
                }

                _logger.LogWarning("Cannot reprocess event {Id} - no audit logger available", deadLetterId);
                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to reprocess dead letter event {Id}", deadLetterId);

                // Update failure information
                deadLetterEvent.Metadata[$"RetryFailure_{deadLetterEvent.RetryCount}"] = ex.Message;
                await SaveDeadLetterEventAsync(deadLetterEvent);

                return false;
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Reprocess all failed events in the dead letter queue.
    /// Note: This is best-effort — the event list is read under lock, but each reprocess
    /// acquires/releases the lock independently. Events may be added or removed between iterations.
    /// </summary>
    public async Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new ReprocessingResult();
        var startTime = DateTimeOffset.UtcNow;

        var events = await GetFailedEventsAsync(int.MaxValue);
        result.TotalEvents = events.Count;

        foreach (var evt in events.Where(static e => !e.IsProcessed))
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var success = await ReprocessEventAsync(evt.Id);
                if (success)
                {
                    result.SuccessfullyProcessed++;
                }
                else
                {
                    result.FailedToProcess++;
                    result.FailedEventIds.Add(evt.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reprocessing event {Id}", evt.Id);
                result.FailedToProcess++;
                result.FailedEventIds.Add(evt.Id);
            }
        }

        result.Duration = DateTimeOffset.UtcNow - startTime;
        return result;
    }

    /// <summary>
    /// Purge processed events from the dead letter queue
    /// </summary>
    public async Task<int> PurgeProcessedEventsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var count = 0;
            var files = Directory.GetFiles(_deadLetterPath, "dlq_*.json");

            foreach (var file in files)
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);

                    if (evt?.IsProcessed != true) continue;
                    // Move to processed folder instead of deleting
                    var processedPath = Path.Combine(_deadLetterPath, "Processed");
                    Directory.CreateDirectory(processedPath);

                    var fileName = Path.GetFileName(file);
                    var destPath = Path.Combine(processedPath, fileName);

                    File.Move(file, destPath, true);
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error purging file {FileName}", Path.GetFileName(file));
                }
            }

            // Clean up old files from the Processed folder that have exceeded retention
            var retentionPath = Path.Combine(_deadLetterPath, "Processed");
            if (Directory.Exists(retentionPath))
            {
                var cutoff = DateTimeOffset.UtcNow - _processedRetention;
                var processedFiles = Directory.GetFiles(retentionPath, "*.json");
                foreach (var file in processedFiles)
                {
                    try
                    {
                        if (new FileInfo(file).CreationTimeUtc < cutoff.UtcDateTime)
                        {
                            File.Delete(file);
                            count++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error deleting expired processed file {FileName}",
                            Path.GetFileName(file));
                    }
                }
            }

            _logger.LogInformation("Purged {Count} processed events from dead letter queue", count);
            return count;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Get statistics about the dead letter queue
    /// </summary>
    public async Task<DeadLetterStatistics> GetStatisticsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var stats = new DeadLetterStatistics();
            var events = await ReadFailedEventsInternalAsync(int.MaxValue);

            stats.TotalEvents = events.Count;
            stats.ProcessedEvents = events.Count(static e => e.IsProcessed);
            stats.PendingEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: 0 });
            stats.FailedEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: > 0 });

            if (events.Any())
            {
                stats.OldestEventDate = events.Min(static e => e.FailedAt);
                stats.NewestEventDate = events.Max(static e => e.FailedAt);

                // Group by event type
                stats.EventsByType = events
                    .Where(static e => e.OriginalEvent != null)
                    .GroupBy(static e => e.OriginalEvent!.EventType)
                    .ToDictionary(static g => g.Key, static g => g.Count());

                // Group by failure reason
                stats.EventsByFailureReason = events
                    .GroupBy(static e => e.FailureReason ?? "Unknown")
                    .ToDictionary(static g => g.Key, static g => g.Count());
            }

            // Calculate total size from actual files
            var files = Directory.GetFiles(_deadLetterPath, "dlq_*.json");
            stats.TotalSizeBytes = files.Sum(static f => new FileInfo(f).Length);

            return stats;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Constructs the file path for a specific dead letter event by its ID.
    /// Uses a deterministic filename convention (dlq_{id}.json) for O(1) lookup
    /// instead of directory scanning.
    /// </summary>
    private string GetFilePathForEvent(string deadLetterId)
    {
        return Path.Combine(_deadLetterPath, $"dlq_{deadLetterId}.json");
    }

    /// <summary>
    /// Save a dead letter event to file using deterministic filename.
    /// </summary>
    private async Task SaveDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        var filePath = GetFilePathForEvent(deadLetterEvent.Id);
        var json = JsonSerializer.Serialize(deadLetterEvent, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// Checks queue size against soft warning threshold and hard capacity cap.
    /// Throws <see cref="InvalidOperationException"/> when the hard cap is reached.
    /// Caller must already hold _fileLock.
    /// </summary>
    private void WarnIfQueueFull()
    {
        var fileCount = Directory.GetFiles(_deadLetterPath, "dlq_*.json").Length;

        // Hard cap enforcement — reject new events to prevent unbounded disk growth
        if (_hardCapacity > 0 && fileCount >= _hardCapacity)
        {
            _logger.LogError(
                "File DLQ hard capacity reached ({Count}/{HardCap}). " +
                "New event rejected. Purge processed events or switch to Redis DLQ.",
                fileCount, _hardCapacity);
            throw new InvalidOperationException(
                $"File DLQ hard capacity reached ({fileCount}/{_hardCapacity}). " +
                "Cannot store additional events. Purge processed events or switch to Redis DLQ.");
        }

        // Soft warning — operational alert before hitting the hard cap
        if (fileCount >= _maxQueueSize)
        {
            _logger.LogWarning(
                "File DLQ has reached {Count} events (soft limit: {Max}, hard cap: {HardCap}). " +
                "Consider switching to Redis DLQ for high-volume scenarios or purging processed events.",
                fileCount, _maxQueueSize, _hardCapacity);
        }
    }

    /// <summary>
    /// Ensure the dead letter directory exists and is writable.
    /// Fails fast at startup if the DLQ cannot function.
    /// </summary>
    private void EnsureDirectoryExists()
    {
        if (!Directory.Exists(_deadLetterPath))
        {
            Directory.CreateDirectory(_deadLetterPath);
            _logger.LogInformation("Created dead letter queue directory at {Path}", _deadLetterPath);
        }

        // Write probe: verify the directory is actually writable.
        // Discovering this at startup is far better than at incident time.
        var probePath = Path.Combine(_deadLetterPath, $".dlq_probe_{Guid.NewGuid()}.tmp");
        try
        {
            File.WriteAllText(probePath, "probe");
            File.Delete(probePath);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Dead letter queue directory '{_deadLetterPath}' exists but is not writable. " +
                "DLQ will not be able to store failed events.", ex);
        }
    }
}
