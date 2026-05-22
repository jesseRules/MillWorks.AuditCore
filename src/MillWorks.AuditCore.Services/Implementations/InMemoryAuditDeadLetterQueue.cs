using System.Collections.Concurrent;
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
/// In-memory implementation for testing and development
/// </summary>
public sealed class InMemoryAuditDeadLetterQueue(
    ILogger<InMemoryAuditDeadLetterQueue> logger,
    IServiceProvider serviceProvider,
    IAuditFieldRedactor fieldRedactor,
    ResilienceOptions? resilienceOptions = null)
    : IAuditDeadLetterQueue
{
    /// <summary>
    /// Whether to include stack traces in the dead letter queue, based on resilience options.
    /// </summary>
    private readonly bool _includeStackTraces = resilienceOptions?.IncludeStackTraces ?? false;

    /// <summary>
    /// Events stored in the dead letter queue
    /// </summary>
    private readonly ConcurrentDictionary<string, DeadLetterAuditEvent> _events = new();

    /// <summary>
    /// Stores a failed audit event in the dead letter queue.
    /// </summary>
    /// <param name="auditEvent"></param>
    /// <param name="exception"></param>
    /// <param name="reason"></param>
    /// <returns></returns>
    public Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        var redactedEvent = AuditEventRedactionHelper.RedactEvent(fieldRedactor, auditEvent);

        DeadLetterAuditEvent deadLetterEvent = new DeadLetterAuditEvent
        {
            OriginalEvent = redactedEvent,
            FailureReason = reason ?? "Unknown",
            ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
            ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
            ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces)
        };

        _events[deadLetterEvent.Id] = deadLetterEvent;
        logger.LogWarning("Stored audit event {EventId} in in-memory dead letter queue", auditEvent.EventId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Stores a failed audit entity in the dead letter queue.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="exception"></param>
    /// <param name="reason"></param>
    /// <returns></returns>
    public Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null, string? reason = null)
    {
        DeadLetterAuditEvent deadLetterEvent = new DeadLetterAuditEvent
        {
            OriginalEntity = entity,
            FailureReason = reason ?? "Unknown",
            ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
            ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
            ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces)
        };

        _events[deadLetterEvent.Id] = deadLetterEvent;
        logger.LogWarning("Stored audit entity {EventId} in in-memory dead letter queue", entity.EventId);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the most recent failed events from the dead letter queue.
    /// </summary>
    /// <param name="maxCount"></param>
    /// <returns></returns>
    public Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
    {
        List<DeadLetterAuditEvent> events = _events.Values
            .OrderByDescending(static e => e.FailedAt)
            .Take(maxCount)
            .ToList();

        return Task.FromResult(events);
    }

    /// <summary>
    /// Gets failed events within a specific date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <returns></returns>
    public Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate, DateTimeOffset endDate)
    {
        List<DeadLetterAuditEvent> events = _events.Values
            .Where(e => e.FailedAt >= startDate && e.FailedAt <= endDate)
            .OrderByDescending(static e => e.FailedAt)
            .ToList();

        return Task.FromResult(events);
    }

    /// <summary>
    /// Reprocesses a specific dead letter event by its ID.
    /// </summary>
    /// <param name="deadLetterId"></param>
    /// <returns></returns>
    public async Task<bool> ReprocessEventAsync(string deadLetterId)
    {
        if (!_events.TryGetValue(deadLetterId, out DeadLetterAuditEvent? deadLetterEvent))
        {
            logger.LogWarning("Dead letter event {Id} not found", deadLetterId);
            return false;
        }

        deadLetterEvent.RetryCount++;
        deadLetterEvent.LastRetryAt = DateTimeOffset.UtcNow;

        try
        {
            // Create a scope to get the scoped IAuditLogger
            var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();
            using var scope = scopeFactory.CreateScope();
            IAuditLogger? auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();

            if (auditLogger == null || deadLetterEvent.OriginalEvent == null) return false;
            await auditLogger.LogAsync(deadLetterEvent.OriginalEvent);
            deadLetterEvent.IsProcessed = true;
            deadLetterEvent.ProcessedAt = DateTimeOffset.UtcNow;

            logger.LogInformation("Successfully reprocessed dead letter event {Id}", deadLetterId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reprocess dead letter event {Id}", deadLetterId);
            return false;
        }
    }

    /// <summary>
    /// Reprocesses all unprocessed dead letter events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default)
    {
        ReprocessingResult result = new ReprocessingResult();
        DateTimeOffset startTime = DateTimeOffset.UtcNow;

        List<DeadLetterAuditEvent> events = _events.Values.Where(static e => !e.IsProcessed).ToList();
        result.TotalEvents = events.Count;

        foreach (DeadLetterAuditEvent evt in events.TakeWhile(_ => !cancellationToken.IsCancellationRequested))
        {
            // Attempt to reprocess each event
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (evt.IsProcessed)
            {
                continue;
            }

            if (evt.OriginalEvent == null)
            {
                result.FailedEventIds.Add(evt.Id);
                continue;
            }

            bool success = await ReprocessEventAsync(evt.Id);
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

        result.Duration = DateTimeOffset.UtcNow - startTime;
        return result;
    }

    /// <summary>
    /// Purges all processed events from the dead letter queue.
    /// </summary>
    /// <returns></returns>
    public Task<int> PurgeProcessedEventsAsync()
    {
        List<string> processedEvents = _events.Values
            .Where(static e => e.IsProcessed)
            .Select(static e => e.Id)
            .ToList();

        foreach (string id in processedEvents)
        {
            _events.TryRemove(id, out _);
        }

        logger.LogInformation("Purged {Count} processed events from in-memory dead letter queue",
            processedEvents.Count);
        return Task.FromResult(processedEvents.Count);
    }

    /// <summary>
    /// Gets statistics about the dead letter queue.
    /// </summary>
    /// <returns></returns>
    public Task<DeadLetterStatistics> GetStatisticsAsync()
    {
        List<DeadLetterAuditEvent> events = _events.Values.ToList();

        DeadLetterStatistics stats = new DeadLetterStatistics
        {
            TotalEvents = events.Count,
            ProcessedEvents = events.Count(static e => e.IsProcessed),
            PendingEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: 0 }),
            FailedEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: > 0 })
        };

        if (events.Any())
        {
            stats.OldestEventDate = events.Min(static e => e.FailedAt);
            stats.NewestEventDate = events.Max(static e => e.FailedAt);

            stats.EventsByType = events
                .Where(static e => e.OriginalEvent != null)
                .GroupBy(static e => e.OriginalEvent?.EventType)
                .ToDictionary(static g => g.Key ?? "Unknown", static g => g.Count());

            stats.EventsByFailureReason = events
                .GroupBy(static e => e.FailureReason ?? "Unknown")
                .ToDictionary(static g => g.Key, static g => g.Count());
        }

        // Estimate size (rough approximation)
        stats.TotalSizeBytes = events.Count * 1024; // Assume 1KB per event

        return Task.FromResult(stats);
    }
}
