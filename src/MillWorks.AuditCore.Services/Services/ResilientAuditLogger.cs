using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Services;

/// <summary>
/// Resilient Audit Logger that integrates Dead Letter Queue for failure handling.
/// <para>
/// <see cref="LogAsync(AuditEvent, CancellationToken)"/> and
/// <see cref="LogBatchAsync"/> create a fresh DI scope per retry attempt and resolve
/// a fresh <see cref="AuditLogger"/> (with a fresh <c>AuditDbContext</c>)
/// from it. Without scope-per-retry, a failed attempt leaves the
/// <c>AuditEventEntity</c> in the scoped context's identity map; the next attempt
/// re-adds a new instance with the same <c>EventId</c> and EF throws
/// <c>InvalidOperationException</c> ("already being tracked"). The injected
/// <c>innerLogger</c> is retained for <see cref="BeginOperationAsync"/> /
/// <see cref="EndOperationAsync"/> / <see cref="CreateScope"/>, which depend on the
/// same <c>AuditLogger</c> instance for <c>_activeOperations</c> continuity.
/// </para>
/// </summary>
public sealed class ResilientAuditLogger(
    IAuditLogger innerLogger,
    IAuditDeadLetterQueue deadLetterQueue,
    IAuditEventFactory eventFactory,
    IAuditFieldRedactor fieldRedactor,
    IServiceScopeFactory scopeFactory,
    ILogger<ResilientAuditLogger> logger,
    IAuditDiagnostics? diagnostics = null)
    : IAuditLogger
{
    /// <summary>
    /// Maximum number of retries for logging
    /// </summary>
    private readonly int _maxRetries = 3;

    /// <summary>
    /// Base delay for exponential backoff between retry attempts
    /// </summary>
    private readonly TimeSpan _baseRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Logs an audit event with resilience and dead letter queue fallback
    /// </summary>
    public async Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        Exception? lastException = null;
        bool eventWasSaved = false;

        for (int retry = 0; retry <= _maxRetries; retry++)
        {
            try
            {
                // Check for cancellation before attempting
                cancellationToken.ThrowIfCancellationRequested();

                if (retry > 0)
                {
                    // Check if event was already saved in a previous attempt
                    if (eventWasSaved)
                    {
                        logger.LogWarning(
                            "Event {EventId} was already saved but encountered error. Not retrying save.",
                            auditEvent.EventId);
                        return; // Don't retry saves
                    }

                    AuditSqlCommandInterceptor.RecordRetry("audit_log");

                    // Exponential backoff with jitter to avoid thundering herd
                    var exponentialDelay = _baseRetryDelay.TotalMilliseconds * Math.Pow(2, retry - 1);
                    var jitter = Random.Shared.Next(0, (int)(exponentialDelay * 0.3));
                    await Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
                    logger.LogDebug("Retrying audit log for event {EventId}, attempt {Attempt}",
                        auditEvent.EventId, retry + 1);
                }

                // Fresh DI scope per attempt: a prior failed attempt may have left the
                // AuditEventEntity tracked on its scoped DbContext, which would throw
                // "already being tracked" on the next AddAsync with the same EventId.
                using (var scope = scopeFactory.CreateScope())
                {
                    var attemptLogger = scope.ServiceProvider.GetRequiredService<AuditLogger>();
                    await attemptLogger.LogAsync(auditEvent, cancellationToken);
                }
                eventWasSaved = true; // Mark as saved

                if (retry > 0)
                {
                    logger.LogInformation("Successfully logged audit event {EventId} after {Attempts} attempts",
                        auditEvent.EventId, retry + 1);
                }

                return;
            }
            catch (OperationCanceledException)
            {
                // Don't retry or send to DLQ on cancellation - just propagate
                throw;
            }
            catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
            {
                // Duplicate key error - event was already saved
                logger.LogWarning(
                    "Event {EventId} already exists in database. Treating as success.",
                    auditEvent.EventId);
                return; // Don't retry or send to DLQ
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Failed to log audit event {EventId}, attempt {Attempt} of {MaxAttempts}",
                    auditEvent.EventId, retry + 1, _maxRetries + 1);

                // If we got here, we don't know if it saved or not
                // For safety, assume it didn't save and allow retry
            }
        }

        // All retries failed - send to dead letter queue
        try
        {
            diagnostics?.Increment(AuditDiagnosticCounter.DlqStoreOperation);
            await deadLetterQueue.StoreFailedEventAsync(
                auditEvent,
                lastException,
                $"Failed after {_maxRetries + 1} attempts");

            logger.LogError(lastException,
                "Audit event {EventId} sent to dead letter queue after all retries failed",
                auditEvent.EventId);
        }
        catch (Exception dlqEx)
        {
            diagnostics?.Increment(AuditDiagnosticCounter.DlqStoreFailure);

            // Critical failure - even DLQ failed
            // Log only EventId and EventType — not {@Event} which would serialize
            // unredacted CustomFields/Target (potential PHI) to the ILogger sink.
            logger.LogCritical(dlqEx,
                "CRITICAL: Failed to store audit event {EventId} (type: {EventType}) in dead letter queue. See emergency fallback file.",
                auditEvent.EventId, auditEvent.EventType);

            diagnostics?.Increment(AuditDiagnosticCounter.EmergencyFallbackWrite);
            await EmergencyFallbackAsync(auditEvent, dlqEx);
        }
    }

    /// <summary>
    /// Logs a batch of audit events with resilience and dead letter queue fallback.
    /// Retries the entire batch on failure. On exhaustion, sends each event individually to DLQ.
    /// </summary>
    public async Task<BatchAuditResult> LogBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvents);

        if (auditEvents.Any(static e => e is null))
            throw new ArgumentException("Batch cannot contain null audit events.", nameof(auditEvents));

        if (auditEvents.Count == 0)
            return BatchAuditResult.Succeeded(0);

        Exception? lastException = null;

        for (int retry = 0; retry <= _maxRetries; retry++)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (retry > 0)
                {
                    AuditSqlCommandInterceptor.RecordRetry("audit_log_batch");

                    var exponentialDelay = _baseRetryDelay.TotalMilliseconds * Math.Pow(2, retry - 1);
                    var jitter = Random.Shared.Next(0, (int)(exponentialDelay * 0.3));
                    await Task.Delay(TimeSpan.FromMilliseconds(exponentialDelay + jitter), cancellationToken);
                    logger.LogDebug("Retrying batch audit log ({Count} events), attempt {Attempt}",
                        auditEvents.Count, retry + 1);
                }

                // Fresh DI scope per attempt — same rationale as LogAsync above. A
                // mid-batch transaction failure strands every added AuditEventEntity in
                // the scoped DbContext's identity map.
                BatchAuditResult result;
                using (var scope = scopeFactory.CreateScope())
                {
                    var attemptLogger = scope.ServiceProvider.GetRequiredService<AuditLogger>();
                    result = await attemptLogger.LogBatchAsync(auditEvents, cancellationToken);
                }

                if (retry > 0)
                {
                    logger.LogInformation("Successfully logged batch of {Count} audit events after {Attempts} attempts",
                        auditEvents.Count, retry + 1);
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
            {
                logger.LogWarning("Batch contains duplicate keys. Treating as success.");
                return BatchAuditResult.Succeeded(auditEvents.Count);
            }
            catch (Exception ex)
            {
                lastException = ex;
                logger.LogWarning(ex, "Failed to log batch of {Count} audit events, attempt {Attempt} of {MaxAttempts}",
                    auditEvents.Count, retry + 1, _maxRetries + 1);
            }
        }

        // All retries failed — send each event individually to DLQ
        logger.LogError(lastException,
            "Batch of {Count} audit events failed after all retries. Sending individually to DLQ.",
            auditEvents.Count);

        foreach (var auditEvent in auditEvents)
        {
            try
            {
                diagnostics?.Increment(AuditDiagnosticCounter.DlqStoreOperation);
                await deadLetterQueue.StoreFailedEventAsync(
                    auditEvent,
                    lastException,
                    $"Batch failed after {_maxRetries + 1} attempts");
            }
            catch (Exception dlqEx)
            {
                diagnostics?.Increment(AuditDiagnosticCounter.DlqStoreFailure);
                logger.LogCritical(dlqEx,
                    "CRITICAL: Failed to store audit event {EventId} (type: {EventType}) in dead letter queue. See emergency fallback file.",
                    auditEvent.EventId, auditEvent.EventType);
                diagnostics?.Increment(AuditDiagnosticCounter.EmergencyFallbackWrite);
                await EmergencyFallbackAsync(auditEvent, dlqEx);
            }
        }

        return BatchAuditResult.Failed(auditEvents, lastException!);
    }

    /// <summary>
    /// Logs an audit event with the specified type and data
    /// </summary>
    public async Task LogAsync(string eventType, object? data = null, CancellationToken cancellationToken = default)
    {
        var auditEvent = eventFactory.CreateEvent(eventType, data);
        await LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Logs an audit event with the specified type, message, and data
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="message"></param>
    /// <param name="data"></param>
    /// <param name="cancellationToken"></param>
    public async Task LogAsync(string eventType, string message, Dictionary<string, object?> data,
        CancellationToken cancellationToken)
    {
        var auditEvent = eventFactory.CreateEvent(eventType, data);
        auditEvent.CustomFields["Message"] = message;
        await LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Begins an operation and returns its ID
    /// </summary>
    public async Task<Guid> BeginOperationAsync(string operationType, object? metadata = null)
    {
        try
        {
            return await innerLogger.BeginOperationAsync(operationType, metadata);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to begin operation {OperationType}", operationType);

            // Create a dummy operation ID and log to DLQ.
            // Redact CustomFields before storage — ex.Message and caller-supplied metadata
            // can contain sensitive data (SQL errors, connection strings, PHI).
            var operationId = Guid.NewGuid();
            var auditEvent = new AuditEvent
            {
                EventId = operationId,
                EventType = $"{operationType}.Failed",
                StartDate = DateTimeOffset.UtcNow,
                CustomFields = fieldRedactor.RedactFields(new Dictionary<string, object?>
                {
                    ["OperationType"] = operationType,
                    ["Metadata"] = metadata,
                    ["FailureReason"] = ex.GetType().Name
                })
            };

            await deadLetterQueue.StoreFailedEventAsync(auditEvent, ex, "Failed to begin operation");
            return operationId;
        }
    }

    /// <summary>
    /// Ends an operation with the specified ID
    /// </summary>
    public async Task EndOperationAsync(Guid operationId, bool success = true, object? result = null)
    {
        try
        {
            await innerLogger.EndOperationAsync(operationId, success, result);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end operation {OperationId}", operationId);

            // Log the end operation failure to DLQ.
            // Redact CustomFields before storage — ex.Message and caller-supplied result
            // can contain sensitive data (SQL errors, connection strings, PHI).
            var auditEvent = new AuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = "Operation.EndFailed",
                StartDate = DateTimeOffset.UtcNow,
                CustomFields = fieldRedactor.RedactFields(new Dictionary<string, object?>
                {
                    ["OperationId"] = operationId,
                    ["Success"] = success,
                    ["Result"] = result,
                    ["FailureReason"] = ex.GetType().Name
                })
            };

            await deadLetterQueue.StoreFailedEventAsync(auditEvent, ex, "Failed to end operation");
        }
    }

    /// <summary>
    /// Creates a new audit scope for the specified event type
    /// </summary>
    public ICustomAuditScope CreateScope(string eventType, object? target = null)
    {
        try
        {
            var innerScope = innerLogger.CreateScope(eventType, target);
            return new ResilientAuditScope(innerScope, deadLetterQueue, logger);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create audit scope for {EventType}", eventType);

            // Return a no-op scope that logs to DLQ
            return new DeadLetterAuditScope(eventType, target, deadLetterQueue, logger);
        }
    }

    /// <summary>
    /// Emergency fallback when even DLQ fails.
    /// ILogger.LogCritical (called before this method) is the primary fallback.
    /// This method attempts a file-based backup using container-safe paths.
    /// </summary>
    private async Task EmergencyFallbackAsync(AuditEvent auditEvent, Exception exception)
    {
        try
        {
            var basePath = Path.GetTempPath();
            var emergencyPath = Path.Combine(basePath, "MillWorks.Audit", "AuditEmergency");

            Directory.CreateDirectory(emergencyPath);

            var fileName = $"emergency_{auditEvent.EventId}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.json";
            var filePath = Path.Combine(emergencyPath, fileName);

            // Redact sensitive data before writing to the temp directory.
            // Emergency files may be world-readable in containerized environments,
            // so apply the same redaction as normal persistence.
            var redactedCustomFields = fieldRedactor.RedactFields(auditEvent.CustomFields);
            var redactedTarget = fieldRedactor.RedactTarget(auditEvent.Target);

            var emergencyData = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Event = new
                {
                    auditEvent.EventId,
                    auditEvent.EventType,
                    auditEvent.StartDate,
                    auditEvent.EndDate,
                    Target = redactedTarget,
                    CustomFields = redactedCustomFields,
                    auditEvent.Success,
                    auditEvent.ErrorMessage
                },
                Error = exception.GetType().Name // Exclude full stack trace from temp files
            };

            await File.WriteAllTextAsync(filePath,
                System.Text.Json.JsonSerializer.Serialize(emergencyData));

            // Restrict file permissions on Unix to owner-only (read+write).
            // Temp directories may be world-readable in containerized environments.
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // ILogger.LogCritical already fired before this method was called.
            // If even temp file writing fails, there's nothing more we can do.
        }
    }

}
