using StackExchange.Redis;
using System.Text.Json;
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
/// Redis-based Dead Letter Queue for handling failed audit messages.
/// Uses a sorted set as a time-ordered index of event IDs and a hash for O(1) payload lookup.
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
    /// Sorted set key — members are event IDs (short strings), scores are Unix timestamps.
    /// Used only as a time-ordered index.
    /// </summary>
    private readonly string _indexKey;

    /// <summary>
    /// Hash key — field = event ID, value = full JSON payload.
    /// Provides O(1) get/set/delete by ID.
    /// </summary>
    private readonly string _dataKey;

    /// <summary>
    /// Message expiry duration
    /// </summary>
    private readonly TimeSpan _messageExpiry;

    /// <summary>
    /// Field redactor for sanitizing sensitive data before DLQ storage
    /// </summary>
    private readonly IAuditFieldRedactor _fieldRedactor;

    /// <summary>
    /// Whether to include full stack traces in DLQ entries
    /// </summary>
    private readonly bool _includeStackTraces;

    /// <summary>
    /// Service scope factory for resolving scoped IAuditLogger during replay
    /// </summary>
    private readonly IServiceScopeFactory? _serviceScopeFactory;

    /// <summary>
    /// Redis Audit Dead Letter Queue Constructor
    /// </summary>
    public RedisAuditDeadLetterQueue(
        IConnectionMultiplexer redis,
        IAuditFieldRedactor fieldRedactor,
        ILogger<RedisAuditDeadLetterQueue>? logger = null,
        string queueName = "audit:dlq",
        TimeSpan? messageExpiry = null,
        ResilienceOptions? resilienceOptions = null,
        IServiceScopeFactory? serviceScopeFactory = null)
    {
        IConnectionMultiplexer redis1 = redis ?? throw new ArgumentNullException(nameof(redis));
        _fieldRedactor = fieldRedactor ?? throw new ArgumentNullException(nameof(fieldRedactor));
        _logger = logger;
        _db = redis1.GetDatabase();
        _indexKey = $"{queueName}:index";
        _dataKey = $"{queueName}:data";
        _messageExpiry = messageExpiry ?? TimeSpan.FromDays(30);
        _includeStackTraces = resilienceOptions?.IncludeStackTraces ?? false;
        _serviceScopeFactory = serviceScopeFactory;

        // Startup validation: verify Redis is reachable.
        // Discovering this at startup is far better than at incident time.
        if (!redis1.IsConnected)
        {
            _logger?.LogError(
                "Redis DLQ: connection multiplexer is not connected at startup. " +
                "DLQ will not be able to store failed events until Redis is reachable.");
        }
    }

    #region IAuditDeadLetterQueue Implementation

    /// <summary>
    /// Stores a failed audit event in the dead letter queue
    /// </summary>
    public async Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        var redactedEvent = AuditEventRedactionHelper.RedactEvent(_fieldRedactor, auditEvent);

        var deadLetterEvent = new DeadLetterAuditEvent
        {
            Id = Guid.NewGuid().ToString(),
            OriginalEventId = auditEvent.EventId.ToString(),
            OriginalEvent = redactedEvent,
            FailureReason = reason ?? "Unknown failure",
            ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
            ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
            ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces),
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
            ExceptionType = ExceptionDiagnosticHelper.GetExceptionType(exception),
            ExceptionMessage = ExceptionDiagnosticHelper.GetTruncatedMessage(exception),
            ExceptionStackTrace = ExceptionDiagnosticHelper.GetStackTrace(exception, _includeStackTraces),
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
        // Get event IDs from the time-ordered index (most recent first)
        var ids = await _db.SortedSetRangeByScoreAsync(
            _indexKey,
            order: Order.Descending,
            take: maxCount);

        return await BatchGetEventsAsync(ids);
    }

    /// <summary>
    /// Gets failed events within a specific date range
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate,
        DateTimeOffset endDate)
    {
        var start = startDate.ToUnixTimeSeconds();
        var stop = endDate.ToUnixTimeSeconds();

        // Get event IDs within the time range from the index
        var ids = await _db.SortedSetRangeByScoreAsync(
            _indexKey,
            start,
            stop);

        return await BatchGetEventsAsync(ids);
    }

    /// <summary>
    /// Reprocesses a specific event in the dead letter queue by replaying it
    /// through <see cref="IAuditLogger"/>. Only marks the event as processed
    /// after the replay succeeds.
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

            try
            {
                if (_serviceScopeFactory == null)
                {
                    _logger?.LogWarning(
                        "Cannot reprocess event {Id} - no IServiceScopeFactory available. " +
                        "Ensure RedisAuditDeadLetterQueue is registered through DI.",
                        deadLetterId);
                    return false;
                }

                using var scope = _serviceScopeFactory.CreateScope();
                var auditLogger = scope.ServiceProvider.GetService<IAuditLogger>();

                if (auditLogger != null && evt.OriginalEvent != null)
                {
                    await auditLogger.LogAsync(evt.OriginalEvent);

                    // Mark as processed only after replay succeeds
                    evt.IsProcessed = true;
                    evt.ProcessedAt = DateTimeOffset.UtcNow;

                    await UpdateDeadLetterEventAsync(evt);
                    await RemoveEventByIdAsync(deadLetterId);

                    _logger?.LogInformation("Successfully reprocessed dead letter event {Id} from Redis", deadLetterId);
                    return true;
                }

                _logger?.LogWarning("Cannot reprocess event {Id} - no audit logger available or no original event",
                    deadLetterId);
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to reprocess dead letter event {Id} from Redis", deadLetterId);

                // Persist retry failure metadata
                evt.Metadata[$"RetryFailure_{evt.RetryCount}"] = ex.GetType().Name;
                await UpdateDeadLetterEventAsync(evt);

                return false;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to retrieve dead letter event {Id} from Redis", deadLetterId);
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
        var ids = await _db.SortedSetRangeByScoreAsync(_indexKey);
        var events = await BatchGetEventsAsync(ids);
        var removedCount = 0;

        foreach (var evt in events)
        {
            if (evt.IsProcessed)
            {
                await RemoveEventByIdAsync(evt.Id);
                removedCount++;
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
        var ids = await _db.SortedSetRangeByScoreAsync(_indexKey);
        var events = await BatchGetEventsAsync(ids);

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
            TotalSizeBytes = 0
        };

        // Estimate total size from the data hash
        if (ids.Length > 0)
        {
            var values = await _db.HashGetAsync(_dataKey,
                ids.Select(static id => (RedisValue)id).ToArray());
            stats.TotalSizeBytes = values
                .Where(static v => v.HasValue)
                .Sum(static v => System.Text.Encoding.UTF8.GetByteCount(v!));
        }

        return stats;
    }

    #endregion

    #region Private Helper Methods

    /// <summary>
    /// Stores a dead letter event in Redis using the hash+index model.
    /// </summary>
    private async Task StoreDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        var json = JsonSerializer.Serialize(deadLetterEvent);
        var score = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var eventId = deadLetterEvent.Id;

        // Store full payload in the data hash — O(1)
        await _db.HashSetAsync(_dataKey, eventId, json);

        // Add event ID to the time-ordered index — O(log n)
        await _db.SortedSetAddAsync(_indexKey, eventId, score);

        // Apply expiry to both keys
        await _db.KeyExpireAsync(_indexKey, _messageExpiry);
        await _db.KeyExpireAsync(_dataKey, _messageExpiry);
    }

    /// <summary>
    /// Updates an existing dead letter event in the data hash — O(1).
    /// Score (timestamp) stays the same since it represents original failure time.
    /// </summary>
    private async Task UpdateDeadLetterEventAsync(DeadLetterAuditEvent deadLetterEvent)
    {
        var json = JsonSerializer.Serialize(deadLetterEvent);
        await _db.HashSetAsync(_dataKey, deadLetterEvent.Id, json);
    }

    /// <summary>
    /// Gets a specific event by ID — O(1) hash lookup.
    /// </summary>
    private async Task<DeadLetterAuditEvent?> GetEventByIdAsync(string eventId)
    {
        var json = await _db.HashGetAsync(_dataKey, eventId);
        if (json.IsNullOrEmpty) return null;

        try
        {
            return JsonSerializer.Deserialize<DeadLetterAuditEvent>((string)json!);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(ex, "Failed to deserialize dead letter event {Id} from Redis", eventId);
            return null;
        }
    }

    /// <summary>
    /// Removes an event by ID — O(1) for hash, O(log n) for sorted set.
    /// </summary>
    private async Task<bool> RemoveEventByIdAsync(string eventId)
    {
        var removed = await _db.HashDeleteAsync(_dataKey, eventId);
        if (removed)
        {
            await _db.SortedSetRemoveAsync(_indexKey, eventId);
        }

        return removed;
    }

    /// <summary>
    /// Batch-fetches events from the data hash for a set of IDs.
    /// Uses a single HMGET round-trip for efficiency.
    /// </summary>
    private async Task<List<DeadLetterAuditEvent>> BatchGetEventsAsync(RedisValue[] ids)
    {
        if (ids.Length == 0)
            return [];

        var values = await _db.HashGetAsync(_dataKey, ids);
        var events = new List<DeadLetterAuditEvent>(ids.Length);

        for (int i = 0; i < values.Length; i++)
        {
            if (values[i].IsNullOrEmpty) continue;

            try
            {
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>((string)values[i]!);
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
    /// Clears expired messages from the queue
    /// </summary>
    public async Task<long> CleanupExpiredMessagesAsync(CancellationToken cancellationToken = default)
    {
        var expiryDate = DateTimeOffset.UtcNow.Add(-_messageExpiry);
        var expiryScore = expiryDate.ToUnixTimeSeconds();

        // Get IDs of expired events from the index (score < expiry threshold)
        var expiredIds = await _db.SortedSetRangeByScoreAsync(
            _indexKey,
            stop: expiryScore);

        if (expiredIds.Length == 0)
            return 0;

        // Remove from data hash
        await _db.HashDeleteAsync(_dataKey,
            expiredIds.Select(static id => (RedisValue)id).ToArray());

        // Remove from index
        long removedCount = 0;
        foreach (var id in expiredIds)
        {
            if (await _db.SortedSetRemoveAsync(_indexKey, id))
                removedCount++;
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
