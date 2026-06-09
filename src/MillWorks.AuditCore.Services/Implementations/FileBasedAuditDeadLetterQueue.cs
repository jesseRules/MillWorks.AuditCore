using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Core;
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
///   <item>Uses in-process locking — not safe across multiple processes.</item>
///   <item>Statistics and lookups use an in-memory index (O(1)). Full event reads still hit disk.</item>
///   <item>Soft warning threshold: <see cref="ResilienceOptions.FileBasedMaxQueueSize"/> (default 1000).</item>
///   <item>Hard capacity cap: <see cref="ResilienceOptions.FileBasedHardCapacity"/> (default 5000).
///     New events are rejected when this limit is reached.</item>
///   <item>Processed files are retained in a <c>Processed/</c> subfolder for
///     <see cref="ResilienceOptions.ProcessedRetention"/> (default 24 hours) before cleanup.</item>
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

    // _fileLock replaced by _writeLock + ConcurrentDictionary index — see Fix 7

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
    /// In-memory index of DLQ files. Eliminates O(n) directory scans for stats, lookups, and purge.
    /// Built lazily on first access; maintained incrementally on mutations.
    /// </summary>
    private readonly ConcurrentDictionary<string, DlqFileEntry> _fileIndex = new();
    private volatile bool _indexBuilt;

    /// <summary>
    /// Write lock for file I/O and index mutations. Read operations against the
    /// ConcurrentDictionary do not require this lock after the index is built.
    /// </summary>
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private sealed record DlqFileEntry(
        string FilePath, string EventId, DateTimeOffset FailedAt, bool IsProcessed,
        int RetryCount, string? EventType, string? FailureReason, long FileSize,
        DateTimeOffset? ProcessedAt = null);

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
        _processedRetention = resilienceOptions?.ProcessedRetention ?? TimeSpan.FromHours(24);
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
    /// Lazily builds the in-memory index from disk on first access.
    /// Uses _writeLock for thread-safe initialization.
    /// </summary>
    private async Task EnsureIndexBuiltAsync()
    {
        if (_indexBuilt) return;

        await _writeLock.WaitAsync();
        try
        {
            if (_indexBuilt) return;

            foreach (var file in Directory.GetFiles(_deadLetterPath, "dlq_*.json"))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(file);
                    var envelope = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);
                    if (envelope is not null)
                    {
                        _fileIndex[envelope.Id] = new DlqFileEntry(
                            file, envelope.Id, envelope.FailedAt, envelope.IsProcessed,
                            envelope.RetryCount, envelope.OriginalEvent?.EventType,
                            envelope.FailureReason, new FileInfo(file).Length);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to index dead letter file {FileName}", Path.GetFileName(file));
                }
            }

            var processedPath = Path.Combine(_deadLetterPath, "Processed");
            if (Directory.Exists(processedPath))
            {
                foreach (var file in Directory.GetFiles(processedPath, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var envelope = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);
                        if (envelope is not null)
                        {
                            _fileIndex[envelope.Id] = new DlqFileEntry(
                                file, envelope.Id, envelope.FailedAt, true,
                                envelope.RetryCount, envelope.OriginalEvent?.EventType,
                                envelope.FailureReason, new FileInfo(file).Length,
                                envelope.ProcessedAt);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to index processed file {FileName}", Path.GetFileName(file));
                    }
                }
            }

            _indexBuilt = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Store a failed audit event in the dead letter queue
    /// </summary>
    public async Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
    {
        var redactedEvent = AuditEventRedactionHelper.RedactEvent(_fieldRedactor, auditEvent);
        await EnsureIndexBuiltAsync();

        await _writeLock.WaitAsync();
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

            // Update index
            var filePath = GetFilePathForEvent(deadLetterEvent.Id);
            _fileIndex[deadLetterEvent.Id] = new DlqFileEntry(
                filePath, deadLetterEvent.Id, deadLetterEvent.FailedAt, false,
                0, auditEvent.EventType, reason ?? "Unknown",
                new FileInfo(filePath).Length);

            _logger.LogWarning("Audit event {EventId} stored in dead letter queue. Reason: {Reason}",
                auditEvent.EventId, reason);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Store a failed audit entity in the dead letter queue
    /// </summary>
    public async Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null,
        string? reason = null)
    {
        await EnsureIndexBuiltAsync();

        await _writeLock.WaitAsync();
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

            // Update index
            var filePath = GetFilePathForEvent(deadLetterEvent.Id);
            _fileIndex[deadLetterEvent.Id] = new DlqFileEntry(
                filePath, deadLetterEvent.Id, deadLetterEvent.FailedAt, false,
                0, entity.EventType ?? "Unknown", reason ?? "Unknown",
                new FileInfo(filePath).Length);

            _logger.LogWarning("Audit entity {EventId} stored in dead letter queue. Reason: {Reason}",
                entity.EventId, reason);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Get a list of failed events from the dead letter queue.
    /// Uses the in-memory index for file paths instead of directory scanning.
    /// </summary>
    public async Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
    {
        await EnsureIndexBuiltAsync();

        // Read from index — no lock needed for ConcurrentDictionary reads
        var entries = _fileIndex.Values
            .Where(e => !e.IsProcessed)
            .OrderByDescending(e => e.FailedAt)
            .Take(maxCount)
            .ToList();

        return await ReadEventsFromEntriesAsync(entries);
    }

    /// <summary>
    /// Reads full event objects from disk for the given index entries.
    /// </summary>
    private async Task<List<DeadLetterAuditEvent>> ReadEventsFromEntriesAsync(List<DlqFileEntry> entries)
    {
        var events = new List<DeadLetterAuditEvent>(entries.Count);

        foreach (var entry in entries)
        {
            try
            {
                if (!File.Exists(entry.FilePath)) continue;
                var json = await File.ReadAllTextAsync(entry.FilePath);
                var evt = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);
                if (evt != null)
                    events.Add(evt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to read dead letter file {FileName}", Path.GetFileName(entry.FilePath));
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
        await EnsureIndexBuiltAsync();

        var entries = _fileIndex.Values
            .Where(e => !e.IsProcessed && e.FailedAt >= startDate && e.FailedAt <= endDate)
            .ToList();

        return await ReadEventsFromEntriesAsync(entries);
    }

    /// <summary>
    /// Reprocess a specific dead letter event by its ID.
    /// The write lock is held only during file reads/writes, NOT during the
    /// potentially slow LogAsync call to avoid blocking all DLQ operations.
    /// </summary>
    public async Task<bool> ReprocessEventAsync(string deadLetterId)
    {
        await EnsureIndexBuiltAsync();

        DeadLetterAuditEvent? deadLetterEvent;
        string filePath;

        // Phase 1: Read event under lock
        await _writeLock.WaitAsync();
        try
        {
            filePath = GetFilePathForEvent(deadLetterId);
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("Dead letter event {Id} not found", deadLetterId);
                return false;
            }

            var json = await File.ReadAllTextAsync(filePath);
            deadLetterEvent = JsonSerializer.Deserialize<DeadLetterAuditEvent>(json, _jsonOptions);

            if (deadLetterEvent == null)
            {
                _logger.LogError("Failed to deserialize dead letter event {Id}", deadLetterId);
                return false;
            }

            // Update retry information before releasing lock
            deadLetterEvent.RetryCount++;
            deadLetterEvent.LastRetryAt = DateTimeOffset.UtcNow;

            // Persist retry count increment before attempting replay
            await SaveDeadLetterEventAsync(deadLetterEvent);

            // Update index with new retry count
            if (_fileIndex.TryGetValue(deadLetterId, out var entry))
            {
                _fileIndex[deadLetterId] = entry with { RetryCount = deadLetterEvent.RetryCount };
            }
        }
        finally
        {
            _writeLock.Release();
        }

        // Phase 2: Replay without holding lock (may be slow)
        bool replaySucceeded = false;
        Exception? replayException = null;

        try
        {
            using var scope = _serviceScopeFactory.CreateScope();
            // Resolve the undecorated AuditLogger directly, NOT IAuditLogger.
            // IAuditLogger may be decorated by ResilientAuditLogger, which catches
            // failures and routes back to DLQ without throwing — causing reprocessing
            // to report success when the event is actually still in DLQ.
            var auditLogger = scope.ServiceProvider.GetService<AuditLogger>();

            if (auditLogger != null && deadLetterEvent.OriginalEvent != null)
            {
                await auditLogger.LogAsync(deadLetterEvent.OriginalEvent);
                replaySucceeded = true;
            }
            else
            {
                _logger.LogWarning("Cannot reprocess event {Id} - no audit logger available", deadLetterId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reprocess dead letter event {Id}", deadLetterId);
            replayException = ex;
        }

        // Phase 3: Update final state under lock
        await _writeLock.WaitAsync();
        try
        {
            if (replaySucceeded)
            {
                deadLetterEvent.IsProcessed = true;
                var processedAt = DateTimeOffset.UtcNow;
                deadLetterEvent.ProcessedAt = processedAt;
                await SaveDeadLetterEventAsync(deadLetterEvent);

                if (_fileIndex.TryGetValue(deadLetterId, out var entry))
                {
                    _fileIndex[deadLetterId] = entry with { IsProcessed = true, ProcessedAt = processedAt };
                }

                _logger.LogInformation("Successfully reprocessed dead letter event {Id}", deadLetterId);
                return true;
            }

            if (replayException != null)
            {
                deadLetterEvent.Metadata[$"RetryFailure_{deadLetterEvent.RetryCount}"] = replayException.Message;
                await SaveDeadLetterEventAsync(deadLetterEvent);
            }

            return false;
        }
        finally
        {
            _writeLock.Release();
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
        await EnsureIndexBuiltAsync();

        await _writeLock.WaitAsync();
        try
        {
            var count = 0;

            // Use index to find processed events instead of scanning + deserializing every file
            var processedEntries = _fileIndex.Values
                .Where(e => e.IsProcessed && e.FilePath.StartsWith(_deadLetterPath)
                            && !e.FilePath.Contains(Path.Combine(_deadLetterPath, "Processed")))
                .ToList();

            foreach (var entry in processedEntries)
            {
                try
                {
                    if (!File.Exists(entry.FilePath)) continue;

                    var processedPath = Path.Combine(_deadLetterPath, "Processed");
                    Directory.CreateDirectory(processedPath);

                    var fileName = Path.GetFileName(entry.FilePath);
                    var destPath = Path.Combine(processedPath, fileName);

                    File.Move(entry.FilePath, destPath, true);

                    // Update index with new path
                    _fileIndex[entry.EventId] = entry with { FilePath = destPath };
                    count++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error purging file {FileName}", Path.GetFileName(entry.FilePath));
                }
            }

            // Clean up old files from the Processed folder that have exceeded retention.
            // Use ProcessedAt (when it was successfully replayed), not FailedAt (when it first failed).
            var cutoff = DateTimeOffset.UtcNow - _processedRetention;
            var expiredEntries = _fileIndex.Values
                .Where(e => e.IsProcessed && e.ProcessedAt.HasValue && e.ProcessedAt.Value < cutoff)
                .ToList();

            foreach (var entry in expiredEntries)
            {
                try
                {
                    if (File.Exists(entry.FilePath))
                    {
                        File.Delete(entry.FilePath);
                        count++;
                    }
                    _fileIndex.TryRemove(entry.EventId, out _);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error deleting expired processed file {FileName}",
                        Path.GetFileName(entry.FilePath));
                }
            }

            _logger.LogInformation("Purged {Count} processed events from dead letter queue", count);
            return count;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Get statistics about the dead letter queue
    /// </summary>
    public async Task<DeadLetterStatistics> GetStatisticsAsync()
    {
        await EnsureIndexBuiltAsync();

        // All stats computed from in-memory index — no directory scan or file I/O needed
        var entries = _fileIndex.Values.ToList();
        var stats = new DeadLetterStatistics
        {
            TotalEvents = entries.Count,
            ProcessedEvents = entries.Count(static e => e.IsProcessed),
            PendingEvents = entries.Count(static e => !e.IsProcessed && e.RetryCount == 0),
            FailedEvents = entries.Count(static e => !e.IsProcessed && e.RetryCount > 0)
        };

        if (entries.Count > 0)
        {
            stats.OldestEventDate = entries.Min(static e => e.FailedAt);
            stats.NewestEventDate = entries.Max(static e => e.FailedAt);

            stats.EventsByType = entries
                .Where(static e => e.EventType != null)
                .GroupBy(static e => e.EventType!)
                .ToDictionary(static g => g.Key, static g => g.Count());

            stats.EventsByFailureReason = entries
                .GroupBy(static e => e.FailureReason ?? "Unknown")
                .ToDictionary(static g => g.Key, static g => g.Count());

            stats.TotalSizeBytes = entries.Sum(static e => e.FileSize);
        }

        return stats;
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
    /// Caller must already hold _writeLock.
    /// </summary>
    private void WarnIfQueueFull()
    {
        var fileCount = _fileIndex.Values.Count(static e => !e.IsProcessed);

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
