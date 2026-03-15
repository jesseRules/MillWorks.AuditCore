using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;

/// <summary>
/// File-based implementation of Dead Letter Queue
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
    /// File-based Audit Dead Letter Queue constructor
    /// </summary>
    /// <param name="logger"></param>
    /// <param name="configuration"></param>
    /// <param name="serviceProvider"></param>
    public FileBasedAuditDeadLetterQueue(
        ILogger<FileBasedAuditDeadLetterQueue> logger,
        IConfiguration configuration,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _configuration = configuration;
        _serviceScopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

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
    /// <param name="auditEvent"></param>
    /// <param name="exception"></param>
    /// <param name="reason"></param>
    public async Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        await _fileLock.WaitAsync();
        try
        {
            var deadLetterEvent = new DeadLetterAuditEvent
            {
                OriginalEvent = auditEvent,
                FailureReason = reason ?? "Unknown",
                ExceptionMessage = exception?.Message,
                ExceptionStackTrace = exception?.StackTrace,
                Metadata = new Dictionary<string, object>
                {
                    ["EventType"] = auditEvent.EventType,
                    ["EventId"] = auditEvent.EventId.ToString(),
                    ["StartDate"] = auditEvent.StartDate
                }
            };

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
    /// <param name="entity"></param>
    /// <param name="exception"></param>
    /// <param name="reason"></param>
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
                ExceptionMessage = exception?.Message,
                ExceptionStackTrace = exception?.StackTrace,
                Metadata = new Dictionary<string, object>
                {
                    ["EventType"] = entity.EventType ?? "Unknown",
                    ["EventId"] = entity.EventId.ToString(),
                    ["InsertedDate"] = entity.InsertedDate ?? DateTimeOffset.MinValue
                }
            };

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
    /// <param name="maxCount"></param>
    /// <returns></returns>
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
        var files = Directory.GetFiles(_deadLetterPath, "*.json")
            .OrderByDescending(static f => new FileInfo(f).CreationTimeUtc)
            .Take(maxCount);

        foreach (var file in files)
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
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <returns></returns>
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
    /// <param name="deadLetterId"></param>
    /// <returns></returns>
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
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
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
    /// <returns></returns>
    public async Task<int> PurgeProcessedEventsAsync()
    {
        await _fileLock.WaitAsync();
        try
        {
            var count = 0;
            var files = Directory.GetFiles(_deadLetterPath, "*.json");

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
    /// <returns></returns>
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

            // Calculate total size
            var files = Directory.GetFiles(_deadLetterPath, "*.json");
            stats.TotalSizeBytes = files.Sum(static f => new FileInfo(f).Length);

            return stats;
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Save a dead letter event to file
    /// </summary>
    /// <param name="deadLetterEvent"></param>
    private async Task SaveDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        // Check if a file already exists for this event ID (overwrite on retry/reprocess)
        var existingFiles = Directory.GetFiles(_deadLetterPath, $"dlq_{deadLetterEvent.Id}_*.json");
        var filePath = existingFiles.FirstOrDefault()
                       ?? Path.Combine(_deadLetterPath, $"dlq_{deadLetterEvent.Id}_{deadLetterEvent.FailedAt:yyyyMMddHHmmss}.json");

        var json = JsonSerializer.Serialize(deadLetterEvent, _jsonOptions);
        await File.WriteAllTextAsync(filePath, json);
    }

    /// <summary>
    /// Get the file path for a specific dead letter event by its ID
    /// </summary>
    /// <param name="deadLetterId"></param>
    /// <returns></returns>
    private string GetFilePathForEvent(string deadLetterId)
    {
        var files = Directory.GetFiles(_deadLetterPath, $"dlq_{deadLetterId}_*.json");
        return files.FirstOrDefault() ?? Path.Combine(_deadLetterPath, $"dlq_{deadLetterId}.json");
    }

    /// <summary>
    /// Ensure the dead letter directory exists
    /// </summary>
    private void EnsureDirectoryExists()
    {
        if (Directory.Exists(_deadLetterPath)) return;
        Directory.CreateDirectory(_deadLetterPath);
        _logger.LogInformation("Created dead letter queue directory at {Path}", _deadLetterPath);
    }
}