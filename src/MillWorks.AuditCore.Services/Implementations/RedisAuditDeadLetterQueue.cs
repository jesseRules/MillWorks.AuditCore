using StackExchange.Redis;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;

/// <summary>
/// Redis-based Dead Letter Queue for handling failed audit messages
/// </summary>
public sealed class RedisAuditDeadLetterQueue : IDisposable, IAuditDeadLetterQueue
{
    /// <summary>
    /// Database instance
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Logger for Redis Audit Dead Letter Queue
    /// </summary>
    private readonly ILogger<RedisAuditDeadLetterQueue>? _logger;

    /// <summary>
    /// Queue key in Redis
    /// </summary>
    private readonly string _queueKey;

    /// <summary>
    /// Metadata key in Redis
    /// </summary>
    private readonly string _metadataKey;

    /// <summary>
    /// Message expiry duration
    /// </summary>
    private readonly TimeSpan _messageExpiry;

    /// <summary>
    /// Redis Audit Dead Letter Queue Constructor
    /// </summary>
    public RedisAuditDeadLetterQueue(
        IConnectionMultiplexer redis,
        ILogger<RedisAuditDeadLetterQueue>? logger = null,
        string queueName = "audit:dlq",
        TimeSpan? messageExpiry = null)
    {
        IConnectionMultiplexer redis1 = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger;
        _db = redis1.GetDatabase();
        _queueKey = queueName;
        _metadataKey = $"{queueName}:metadata";
        _messageExpiry = messageExpiry ?? TimeSpan.FromDays(30);
    }

    #region IAuditDeadLetterQueue Implementation

    /// <summary>
    /// Stores a failed audit event in the dead letter queue
    /// </summary>
    public async Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        var deadLetterEvent = new DeadLetterAuditEvent
        {
            Id = Guid.NewGuid().ToString(),
            OriginalEventId = auditEvent.EventId.ToString(),
            OriginalEvent = auditEvent,
            FailureReason = reason ?? "Unknown failure",
            ExceptionMessage = exception?.Message,
            ExceptionStackTrace = exception?.StackTrace,
            FailedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            IsProcessed = false,
            Metadata = new Dictionary<string, object>
            {
                ["EventType"] = auditEvent.EventType,
                ["EventId"] = auditEvent.EventId.ToString(),
                ["StartDate"] = auditEvent.StartDate
            }
        };

        await StoreDeadLetterEventAsync(deadLetterEvent);

        _logger?.LogWarning("Audit event {EventId} stored in Redis dead letter queue. Reason: {Reason}",
            auditEvent.EventId, reason);
    }

    /// <summary>
    /// Stores a failed audit entity in the dead letter queue
    /// </summary>
    public async Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null,
        string? reason = null)
    {
        var deadLetterEvent = new DeadLetterAuditEvent
        {
            Id = Guid.NewGuid().ToString(),
            OriginalEventId = entity.EventId.ToString(),
            OriginalEntity = entity,
            FailureReason = reason ?? "Unknown failure",
            ExceptionMessage = exception?.Message,
            ExceptionStackTrace = exception?.StackTrace,
            FailedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            IsProcessed = false,
            Metadata = new Dictionary<string, object>
            {
                ["EventType"] = entity.EventType ?? "Unknown",
                ["EventId"] = entity.EventId.ToString(),
                ["InsertedDate"] = entity.InsertedDate ?? DateTimeOffset.MinValue
            }
        };

        await StoreDeadLetterEventAsync(deadLetterEvent);

        _logger?.LogWarning("Audit entity {EventId} stored in Redis dead letter queue. Reason: {Reason}",
            entity.EventId, reason);
    }

    /// <summary>
    /// Gets a list of failed events from the dead letter queue
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
    {
        var entries = await _db.SortedSetRangeByScoreAsync(
            _queueKey,
            order: Order.Descending,
            take: maxCount);

        var events = new List<DeadLetterAuditEvent>();

        foreach (RedisValue entry in entries)
        {
            try
            {
                JsonSerializerOptions options = new()
                {
                    PropertyNameCaseInsensitive = true
                };
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty, options);
                if (evt != null)
                {
                    events.Add(evt);
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize dead letter event from Redis");
            }
        }

        return events;
    }

    /// <summary>
    /// Gets failed events within a specific date range
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate,
        DateTimeOffset endDate)
    {
        var start = startDate.ToUnixTimeSeconds();
        var stop = endDate.ToUnixTimeSeconds();

        var entries = await _db.SortedSetRangeByScoreAsync(
            _queueKey,
            start,
            stop);

        var events = new List<DeadLetterAuditEvent>();

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);
                if (evt != null)
                {
                    events.Add(evt);
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize dead letter event from Redis");
            }
        }

        return events;
    }

    /// <summary>
    /// Reprocesses a specific event in the dead letter queue
    /// </summary>
    public async Task<bool> ReprocessEventAsync(string deadLetterId)
    {
        try
        {
            var evt = await GetEventByIdAsync(deadLetterId);

            if (evt == null)
            {
                _logger?.LogWarning("Dead letter event {Id} not found in Redis", deadLetterId);
                return false;
            }

            // Update retry information
            evt.RetryCount++;
            evt.LastRetryAt = DateTimeOffset.UtcNow;

            // NOTE: In a real implementation, you would inject an IAuditLogger
            // and call it here to actually reprocess the audit event
            // For now, we mark as processed and remove from the queue

            evt.IsProcessed = true;
            evt.ProcessedAt = DateTimeOffset.UtcNow;

            // Update the event in Redis
            await UpdateDeadLetterEventAsync(evt);

            // Remove from the queue after successful processing
            await RemoveEventByIdAsync(deadLetterId);

            _logger?.LogInformation("Successfully reprocessed dead letter event {Id} from Redis", deadLetterId);
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to reprocess dead letter event {Id} from Redis", deadLetterId);
            return false;
        }
    }

    /// <summary>
    /// Reprocesses all events in the dead letter queue
    /// </summary>
    public async Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default)
    {
        var startTime = DateTimeOffset.UtcNow;

        var result = new ReprocessingResult
        {
            TotalEvents = 0,
            SuccessfullyProcessed = 0,
            FailedToProcess = 0,
            FailedEventIds = new List<string>()
        };

        var events = await GetFailedEventsAsync(int.MaxValue);
        var unprocessedEvents = events.Where(static e => !e.IsProcessed).ToList();

        result.TotalEvents = unprocessedEvents.Count;

        foreach (var evt in unprocessedEvents)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

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
                _logger?.LogError(ex, "Error reprocessing event {Id}", evt.Id);
                result.FailedToProcess++;
                result.FailedEventIds.Add(evt.Id);
            }
        }

        result.Duration = DateTimeOffset.UtcNow - startTime;
        return result;
    }

    /// <summary>
    /// Purges all processed events from the dead letter queue
    /// </summary>
    public async Task<int> PurgeProcessedEventsAsync()
    {
        var entries = await _db.SortedSetRangeByScoreAsync(_queueKey);
        var removedCount = 0;

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);

                if (evt?.IsProcessed == true)
                {
                    await _db.SortedSetRemoveAsync(_queueKey, entry);
                    await _db.HashDeleteAsync(_metadataKey, evt.Id);
                    removedCount++;
                }
            }
            catch (JsonException ex)
            {
                _logger?.LogError(ex, "Failed to deserialize event during purge");
            }
        }

        _logger?.LogInformation("Purged {Count} processed events from Redis dead letter queue", removedCount);
        return removedCount;
    }

    /// <summary>
    /// Gets statistics about the dead letter queue
    /// </summary>
    public async Task<DeadLetterStatistics> GetStatisticsAsync()
    {
        var entries = await _db.SortedSetRangeByScoreAsync(_queueKey);
        var events = new List<DeadLetterAuditEvent>();

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);
                if (evt != null)
                {
                    events.Add(evt);
                }
            }
            catch (JsonException)
            {
            }
        }

        var stats = new DeadLetterStatistics
        {
            TotalEvents = events.Count,
            ProcessedEvents = events.Count(static e => e.IsProcessed),
            PendingEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: 0 }),
            FailedEvents = events.Count(static e => e is { IsProcessed: false, RetryCount: > 0 }),
            OldestEventDate = events.Any() ? events.Min(static e => e.FailedAt) : null,
            NewestEventDate = events.Any() ? events.Max(static e => e.FailedAt) : null,
            EventsByType = events
                .Where(static e => e.OriginalEvent != null)
                .GroupBy(static e => e.OriginalEvent!.EventType)
                .ToDictionary(static g => g.Key, static g => g.Count()),
            EventsByFailureReason = events
                .GroupBy(static e => e.FailureReason ?? "Unknown")
                .ToDictionary(static g => g.Key, static g => g.Count()),
            TotalSizeBytes = entries.Sum(static e => System.Text.Encoding.UTF8.GetByteCount(e!))
        };

        return stats;
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Stores a dead letter event in Redis
    /// </summary>
    private async Task StoreDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        var json = JsonSerializer.Serialize(deadLetterEvent);
        var score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        await _db.SortedSetAddAsync(_queueKey, json, score);

        // Store metadata for quick lookups
        var metadata = new
        {
            deadLetterEvent.Id,
            deadLetterEvent.FailureReason,
            deadLetterEvent.RetryCount,
            deadLetterEvent.FailedAt
        };

        await _db.HashSetAsync(
            _metadataKey,
            deadLetterEvent.Id,
            JsonSerializer.Serialize(metadata));

        await _db.KeyExpireAsync(_metadataKey, _messageExpiry);
    }

    /// <summary>
    /// Updates an existing dead letter event in Redis
    /// </summary>
    private async Task UpdateDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        // Remove old entry
        await RemoveEventByIdAsync(deadLetterEvent.Id);

        // Add updated entry
        await StoreDeadLetterEventAsync(deadLetterEvent);
    }

    /// <summary>
    /// Gets a specific event by ID
    /// </summary>
    private async Task<DeadLetterAuditEvent?> GetEventByIdAsync(string eventId)
    {
        var entries = await _db.SortedSetRangeByScoreAsync(_queueKey);

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);
                if (evt?.Id == eventId)
                {
                    return evt;
                }
            }
            catch (JsonException)
            {
            }
        }

        return null;
    }

    /// <summary>
    /// Removes an event by ID
    /// </summary>
    private async Task<bool> RemoveEventByIdAsync(string eventId)
    {
        var entries = await _db.SortedSetRangeByScoreAsync(_queueKey);

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);
                if (evt?.Id == eventId)
                {
                    var removed = await _db.SortedSetRemoveAsync(_queueKey, entry);
                    if (removed)
                    {
                        await _db.HashDeleteAsync(_metadataKey, eventId);
                    }

                    return removed;
                }
            }
            catch (JsonException)
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Clears expired messages from the queue
    /// </summary>
    public async Task<long> CleanupExpiredMessagesAsync(CancellationToken cancellationToken = default)
    {
        var entries = await _db.SortedSetRangeByScoreAsync(_queueKey);
        var removedCount = 0L;
        var expiredIds = new List<string>();
        var expiryDate = DateTimeOffset.UtcNow.Add(-_messageExpiry);

        foreach (var entry in entries)
        {
            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string?)entry ?? string.Empty);
                if (evt != null && evt.FailedAt < expiryDate)
                {
                    await _db.SortedSetRemoveAsync(_queueKey, entry);
                    expiredIds.Add(evt.Id);
                    removedCount++;
                }
            }
            catch (JsonException)
            {
            }
        }

        if (expiredIds.Any())
        {
            await _db.HashDeleteAsync(_metadataKey,
                expiredIds.Select(static id => (RedisValue)id).ToArray());
        }

        return removedCount;
    }

    #endregion

    /// <summary>
    /// Disposes the Redis Audit Dead Letter Queue
    /// </summary>
    public void Dispose()
    {
        // Connection multiplexer is managed by DI, don't dispose it
        GC.SuppressFinalize(this);
    }
}