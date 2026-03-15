using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Services;

/// <summary>
/// Resilient Audit Logger that integrates Dead Letter Queue for failure handling
/// </summary>
public sealed class ResilientAuditLogger(
    IAuditLogger innerLogger,
    IAuditDeadLetterQueue deadLetterQueue,
    IAuditEventFactory eventFactory,
    ILogger<ResilientAuditLogger> logger)
    : IAuditLogger
{
    /// <summary>
    /// Maximum number of retries for logging
    /// </summary>
    private readonly int _maxRetries = 3;

    /// <summary>
    /// Retry delay between attempts
    /// </summary>
    private readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Logs an audit event with resilience and dead letter queue fallback
    /// </summary>
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

                    await Task.Delay(_retryDelay * retry, cancellationToken);
                    logger.LogDebug("Retrying audit log for event {EventId}, attempt {Attempt}",
                        auditEvent.EventId, retry + 1);
                }

                await innerLogger.LogAsync(auditEvent, cancellationToken);
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
            catch (DbUpdateException ex) when (IsDuplicateKeyError(ex))
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
            // Critical failure - even DLQ failed
            logger.LogCritical(dlqEx,
                "CRITICAL: Failed to store audit event {EventId} in dead letter queue. Event data: {@Event}",
                auditEvent.EventId, auditEvent);

            // Consider additional fallback like Windows Event Log or file system
            await EmergencyFallbackAsync(auditEvent, dlqEx);
        }
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

            // Create a dummy operation ID and log to DLQ
            var operationId = Guid.NewGuid();
            var auditEvent = new AuditEvent
            {
                EventId = operationId,
                EventType = $"{operationType}.Failed",
                StartDate = DateTimeOffset.UtcNow,
                CustomFields = new Dictionary<string, object?>
                {
                    ["OperationType"] = operationType,
                    ["Metadata"] = metadata,
                    ["FailureReason"] = ex.Message
                }
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

            // Log the end operation failure to DLQ
            var auditEvent = new AuditEvent
            {
                EventId = Guid.NewGuid(),
                EventType = "Operation.EndFailed",
                StartDate = DateTimeOffset.UtcNow,
                CustomFields = new Dictionary<string, object?>
                {
                    ["OperationId"] = operationId,
                    ["Success"] = success,
                    ["Result"] = result,
                    ["FailureReason"] = ex.Message
                }
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

            var emergencyData = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                Event = auditEvent,
                Error = exception.ToString()
            };

            await File.WriteAllTextAsync(filePath,
                System.Text.Json.JsonSerializer.Serialize(emergencyData));
        }
        catch
        {
            // ILogger.LogCritical already fired before this method was called.
            // If even temp file writing fails, there's nothing more we can do.
        }
    }

    /// <summary>
    /// Checks if an exception is a duplicate key error (provider-agnostic)
    /// </summary>
    private static bool IsDuplicateKeyError(DbUpdateException ex)
    {
        return ex.InnerException switch
        {
            SqlException { Number: 2627 or 2601 } => true, // SQL Server
            _ when ex.InnerException?.Message.Contains("UNIQUE constraint") == true => true, // SQLite
            _ when ex.InnerException?.GetType().Name == "PostgresException"
                   && ex.InnerException.Data["SqlState"]?.ToString() == "23505" => true, // PostgreSQL
            _ => false
        };
    }
}