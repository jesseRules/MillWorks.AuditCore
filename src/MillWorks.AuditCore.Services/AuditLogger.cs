using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Core audit logger implementation. Not sealed, and <see cref="LogAsync(AuditEvent, CancellationToken)"/> /
/// <see cref="LogBatchAsync"/> are virtual, so test harnesses can subclass for
/// <c>ResilientAuditLogger</c> scope-per-retry verification without spinning up a full
/// DbContext/repository graph.
/// </summary>
public class AuditLogger(
    ILogger<AuditLogger> logger,
    IAuditEventFactory eventFactory,
    IAuditEventRepository auditEventRepository,
    AuditDbContext dbContext,
    IAuditContext auditContext,
    IAuditFieldRedactor fieldRedactor,
    ITamperDetectionService? tamperDetectionService = null,
    IntegrityWriteBatcher? integrityWriteBatcher = null,
    IOptions<SecurityOptions>? securityOptions = null)
    : IAuditLogger
{
    /// <summary>
    /// Active audit operations tracked by their operation Id
    /// </summary>
    private readonly ConcurrentDictionary<Guid, CustomAuditScope> _activeOperations = new();

    // EnableTamperDetection and EnableBatchedIntegrityWrites drive routing. The injected-service
    // nullness check is a defensive fallback only — if the service isn't registered, the branch
    // is skipped even when the option is true.
    /// <summary>
    /// Security options for audit logging, including tamper detection settings.
    /// </summary>
    private readonly SecurityOptions _securityOptions = securityOptions?.Value ?? new SecurityOptions();

    /// <summary>
    /// Logs an audit event asynchronously
    /// </summary>
    public virtual async Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert to database entity
            var entity = ConvertToEntity(auditEvent);

            if (_securityOptions.EnableTamperDetection && tamperDetectionService is not null)
            {
                var integrityDto = new AuditIntegrityDto
                {
                    EventId = entity.EventId,
                    InsertedDate = entity.InsertedDate,
                    LastUpdatedDate = entity.LastUpdatedDate,
                    JsonData = entity.JsonData,
                    EventType = entity.EventType,
                    User = entity.User,
                    UserId = entity.UserId
                };

                if (_securityOptions.EnableBatchedIntegrityWrites && integrityWriteBatcher is not null)
                {
                    // Batched mode: persist event + durable work item in one transaction.
                    // The work item acts as an outbox — if the batcher crashes or the process
                    // is hard-killed, the work item survives and reconciliation can pick it up.
                    entity.IntegrityStatus = IntegrityStatus.Pending;

                    var workItem = new AuditIntegrityWorkItemEntity
                    {
                        EventId = entity.EventId,
                        Status = IntegrityStatus.Pending
                    };

                    await auditEventRepository.ExecuteInTransactionAsync(async () =>
                    {
                        await auditEventRepository.AddAsync(entity, cancellationToken);
                        dbContext.IntegrityWorkItems.Add(workItem);
                        await auditEventRepository.SaveChangesAsync(cancellationToken);
                    }, cancellationToken);

                    try
                    {
                        await integrityWriteBatcher.EnqueueAsync(integrityDto, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex,
                            "Integrity record batch enqueue failed for event {EventId}. " +
                            "The durable work item is persisted — reconciliation will retry.",
                            entity.EventId);
                    }
                }
                else
                {
                    // Strict mode: wrap event + integrity record in a single transaction
                    // so both succeed or both fail atomically.
                    entity.IntegrityStatus = IntegrityStatus.Completed;
                    await auditEventRepository.ExecuteInTransactionAsync(async () =>
                    {
                        await auditEventRepository.AddAsync(entity, cancellationToken);
                        await auditEventRepository.SaveChangesAsync(cancellationToken);
                        await tamperDetectionService.CreateIntegrityRecordAsync(integrityDto, cancellationToken);
                    }, cancellationToken);
                }
            }
            else
            {
                // No tamper detection — simple insert, no transaction needed.
                // Mark as Completed since no integrity record is expected.
                entity.IntegrityStatus = IntegrityStatus.Completed;
                await auditEventRepository.AddAsync(entity, cancellationToken);
                await auditEventRepository.SaveChangesAsync(cancellationToken);
            }

            logger.LogDebug(
                "Logged audit event of type {EventType} with ID {EventId}",
                auditEvent.EventType,
                auditEvent.EventId);
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // Duplicate key error - this can happen in race conditions or retries
            // Treat as success (idempotent operation)
            logger.LogDebug(
                "Duplicate key for event {EventId}. Treating as success.",
                auditEvent.EventId);
        }
        catch (OperationCanceledException)
        {
            // Silently propagate cancellation — don't log as an error.
            // ResilientAuditLogger has its own OperationCanceledException handler.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to log audit event of type {EventType}",
                auditEvent.EventType);

            // THROW so ResilientAuditLogger can retry and use DLQ
            throw;
        }
    }


    /// <summary>
    /// Logs a batch of audit events atomically.
    /// </summary>
    public virtual async Task<BatchAuditResult> LogBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
    {
        if (auditEvents.Count == 0)
            return BatchAuditResult.Succeeded(0);

        // Single event: delegate to existing LogAsync to avoid regression risk
        if (auditEvents.Count == 1)
        {
            await LogAsync(auditEvents[0], cancellationToken);
            return BatchAuditResult.Succeeded(1);
        }

        try
        {
            var entities = auditEvents.Select(ConvertToEntity).ToList();

            if (_securityOptions.EnableTamperDetection && tamperDetectionService is not null)
            {
                // LogBatchAsync always uses the atomic (strict) path
                foreach (var e in entities)
                    e.IntegrityStatus = IntegrityStatus.Completed;

                await auditEventRepository.ExecuteInTransactionAsync(async () =>
                {
                    await auditEventRepository.AddRangeAsync(entities, cancellationToken);
                    await auditEventRepository.SaveChangesAsync(cancellationToken);

                    var dtos = entities.Select(static e => new AuditIntegrityDto
                    {
                        EventId = e.EventId,
                        InsertedDate = e.InsertedDate,
                        LastUpdatedDate = e.LastUpdatedDate,
                        JsonData = e.JsonData,
                        EventType = e.EventType,
                        User = e.User,
                        UserId = e.UserId
                    }).ToList();

                    await tamperDetectionService.CreateIntegrityRecordBatchAsync(dtos, cancellationToken);
                }, cancellationToken);
            }
            else
            {
                // No tamper detection — mark as Completed since no integrity record is expected
                foreach (var e in entities)
                    e.IntegrityStatus = IntegrityStatus.Completed;

                await auditEventRepository.AddRangeAsync(entities, cancellationToken);
                await auditEventRepository.SaveChangesAsync(cancellationToken);
            }

            logger.LogDebug("Logged batch of {Count} audit events", auditEvents.Count);
            return BatchAuditResult.Succeeded(auditEvents.Count);
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // A duplicate key in an atomic batch means at least one event already exists,
            // but others may be new. Query for existing EventIds in one round-trip, then
            // insert only the new events. This is O(1) queries instead of O(n) per-event.
            logger.LogDebug(
                "Duplicate key in batch of {Count} events. Querying for existing EventIds.",
                auditEvents.Count);

            // Detach any entities that were added to the change tracker before the exception
            foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            {
                if (entry.State == EntityState.Added)
                    entry.State = EntityState.Detached;
            }

            // Single query to find which EventIds already exist
            var allEventIds = auditEvents.Select(e => e.EventId).ToList();
            var existingIds = await auditEventRepository.GetExistingEventIdsAsync(allEventIds, cancellationToken);

            var newEvents = auditEvents.Where(e => !existingIds.Contains(e.EventId)).ToList();
            var duplicateCount = existingIds.Count;

            if (newEvents.Count == 0)
            {
                logger.LogDebug("All {Count} events in batch were duplicates", auditEvents.Count);
                return BatchAuditResult.Duplicate(auditEvents.Count);
            }

            // Insert new events — if this batch also fails with duplicate key, fall back to per-event
            try
            {
                var newEntities = newEvents.Select(ConvertToEntity).ToList();
                foreach (var e in newEntities)
                    e.IntegrityStatus = IntegrityStatus.Completed;

                if (_securityOptions.EnableTamperDetection && tamperDetectionService is not null)
                {
                    await auditEventRepository.ExecuteInTransactionAsync(async () =>
                    {
                        await auditEventRepository.AddRangeAsync(newEntities, cancellationToken);
                        await auditEventRepository.SaveChangesAsync(cancellationToken);

                        var dtos = newEntities.Select(static e => new AuditIntegrityDto
                        {
                            EventId = e.EventId,
                            InsertedDate = e.InsertedDate,
                            LastUpdatedDate = e.LastUpdatedDate,
                            JsonData = e.JsonData,
                            EventType = e.EventType,
                            User = e.User,
                            UserId = e.UserId
                        }).ToList();

                        await tamperDetectionService.CreateIntegrityRecordBatchAsync(dtos, cancellationToken);
                    }, cancellationToken);
                }
                else
                {
                    await auditEventRepository.AddRangeAsync(newEntities, cancellationToken);
                    await auditEventRepository.SaveChangesAsync(cancellationToken);
                }

                logger.LogDebug(
                    "Batch fallback complete: {NewCount} new, {DuplicateCount} duplicates",
                    newEvents.Count, duplicateCount);
                return BatchAuditResult.Succeeded(auditEvents.Count);
            }
            catch (DbUpdateException retryEx) when (DuplicateKeyDetector.IsDuplicateKey(retryEx))
            {
                // Race condition: another process inserted between our query and insert.
                // Fall back to per-event writes for the remaining new events.
                foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
                {
                    if (entry.State == EntityState.Added)
                        entry.State = EntityState.Detached;
                }

                foreach (var auditEvent in newEvents)
                {
                    bool wasDuplicate = await TryLogSingleEventAsync(auditEvent, cancellationToken);
                    if (wasDuplicate)
                        duplicateCount++;
                }

                if (duplicateCount == auditEvents.Count)
                {
                    logger.LogDebug("All {Count} events in batch were duplicates after retry", auditEvents.Count);
                    return BatchAuditResult.Duplicate(auditEvents.Count);
                }

                logger.LogDebug(
                    "Batch fallback complete after retry: {NewCount} new, {DuplicateCount} duplicates",
                    auditEvents.Count - duplicateCount, duplicateCount);
                return BatchAuditResult.Succeeded(auditEvents.Count);
            }
        }
        catch (OperationCanceledException)
        {
            // Silently propagate cancellation — same as LogAsync (see N1 fix)
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to log batch of {Count} audit events", auditEvents.Count);
            throw;
        }
    }

    /// <summary>
    /// Attempts to log a single audit event, returning true if it was a duplicate.
    /// Does not throw on duplicate key; throws on other errors.
    /// </summary>
    private async Task<bool> TryLogSingleEventAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        try
        {
            var entity = ConvertToEntity(auditEvent);

            if (_securityOptions.EnableTamperDetection && tamperDetectionService is not null)
            {
                entity.IntegrityStatus = IntegrityStatus.Completed;
                await auditEventRepository.ExecuteInTransactionAsync(async () =>
                {
                    await auditEventRepository.AddAsync(entity, cancellationToken);
                    await auditEventRepository.SaveChangesAsync(cancellationToken);

                    var dto = new AuditIntegrityDto
                    {
                        EventId = entity.EventId,
                        InsertedDate = entity.InsertedDate,
                        LastUpdatedDate = entity.LastUpdatedDate,
                        JsonData = entity.JsonData,
                        EventType = entity.EventType,
                        User = entity.User,
                        UserId = entity.UserId
                    };
                    await tamperDetectionService.CreateIntegrityRecordAsync(dto, cancellationToken);
                }, cancellationToken);
            }
            else
            {
                entity.IntegrityStatus = IntegrityStatus.Completed;
                await auditEventRepository.AddAsync(entity, cancellationToken);
                await auditEventRepository.SaveChangesAsync(cancellationToken);
            }

            return false; // Not a duplicate
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // Detach the entity that failed so it doesn't pollute subsequent inserts
            foreach (var entry in dbContext.ChangeTracker.Entries().ToList())
            {
                if (entry.State == EntityState.Added)
                    entry.State = EntityState.Detached;
            }
            return true; // Was a duplicate
        }
    }

    /// <summary>
    /// Logs an audit event with the specified type and data
    /// </summary>
    public async Task LogAsync(string eventType, object? data, CancellationToken cancellationToken = default)
    {
        var auditEvent = eventFactory.CreateEvent(eventType, data);
        await LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Logs an audit event with the specified type, message, and data
    /// </summary>
    public async Task LogAsync(string eventType, string message, Dictionary<string, object?> data,
        CancellationToken cancellationToken)
    {
        var auditEvent = eventFactory.CreateEvent(eventType, data);
        auditEvent.CustomFields["Message"] = message;
        await LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Begins a new audit operation and returns its Id
    /// </summary>
    public async Task<Guid> BeginOperationAsync(string operationType, object? metadata = null)
    {
        var operationId = Guid.NewGuid();

        try
        {
            var auditEvent = eventFactory.CreateOperationEvent(operationType, operationId, "Started", metadata);

            // Store in context for child operations
            auditContext.OperationId = operationId;

            var scope = new CustomAuditScope(auditEvent, this, logger);
            _activeOperations[operationId] = scope;

            // Log the start event
            await LogAsync(auditEvent);

            logger.LogDebug("Started audit operation {OperationId} of type {OperationType}",
                operationId, operationType);

            return operationId;
        }
        catch (Exception ex)
        {
            // Remove the scope we just added — LogAsync failed so the operation isn't valid
            _activeOperations.TryRemove(operationId, out _);
            logger.LogError(ex, "Failed to begin audit operation {OperationType}", operationType);
            throw;
        }
    }

    /// <summary>
    /// Ends an audit operation
    /// </summary>
    public async Task EndOperationAsync(Guid operationId, bool success = true, object? result = null)
    {
        try
        {
            if (_activeOperations.TryRemove(operationId, out var scope))
            {
                // Extract operation type from the original event
                var operationType = scope.Event.CustomFields.TryGetValue("OperationType", out var opType)
                    ? opType?.ToString() ?? "UnknownOperation"
                    : scope.Event.EventType.Replace(".Started", "");

                // Create a NEW event for the completion (the start event's EventId is already persisted)
                var completionEvent = eventFactory.CreateOperationEvent(
                    operationType, operationId, success ? "Completed" : "Failed", result);

                completionEvent.EndDate = DateTimeOffset.UtcNow;
                completionEvent.CalculateDuration();
                completionEvent.Success = success;

                // Copy relevant context from the original start event
                foreach (var field in scope.Event.CustomFields)
                {
                    if (!completionEvent.CustomFields.ContainsKey(field.Key))
                        completionEvent.CustomFields[field.Key] = field.Value;
                }

                completionEvent.CustomFields["Status"] = success ? "Completed" : "Failed";

                // Save the completion event
                await LogAsync(completionEvent);

                // Clear from context if this was the current operation
                if (auditContext.OperationId == operationId)
                {
                    auditContext.OperationId = null;
                }

                logger.LogDebug("Ended audit operation {OperationId} with success={Success}",
                    operationId, success);
            }
            else
            {
                // Log as a separate event if scope not found
                var auditEvent = eventFactory.CreateOperationEvent("UnknownOperation", operationId,
                    success ? "Completed" : "Failed", result);
                auditEvent.CustomFields["Warning"] = "Original operation scope not found";

                await LogAsync(auditEvent);

                logger.LogWarning(
                    "Audit operation {OperationId} scope not found, logged as separate event",
                    operationId);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to end audit operation {OperationId}", operationId);
            throw; // Throw for operations
        }
    }

    /// <summary>
    /// Creates a new audit scope for the specified event type and target object
    /// </summary>
    public ICustomAuditScope CreateScope(string eventType, object? target = null)
    {
        var auditEvent = eventFactory.CreateEvent(eventType, target);
        return new CustomAuditScope(auditEvent, this, logger);
    }

    /// <summary>
    /// Converts a CustomAuditEvent to an AuditEventEntity for database storage
    /// </summary>
    private AuditEventEntity ConvertToEntity(AuditEvent auditEvent)
    {
        var entity = new AuditEventEntity
        {
            EventId = auditEvent.EventId,
            EventType = auditEvent.EventType,
            InsertedDate = auditEvent.StartDate,
            LastUpdatedDate = auditEvent.EndDate ?? auditEvent.StartDate,
            Duration = auditEvent.Duration,
            User = auditEvent.Environment.UserName,
            UserEnvName = auditEvent.Environment.UserName,
            MachineName = auditEvent.Environment.MachineName,
            CallingMethodName = auditEvent.Environment.CallingMethodName,
            AssemblyName = auditEvent.Environment.AssemblyName,
            Environment = auditEvent.CustomFields.GetValueOrDefault("Environment")?.ToString() ?? "Production"
        };

        // Map fields: prefer first-class AuditEvent properties when set,
        // fall back to CustomFields. This prevents the DX trap where setting
        // auditEvent.IpAddress directly is silently ignored.
        entity.UserId = auditEvent.UserId
            ?? (auditEvent.CustomFields.TryGetValue("UserId", out var userId) && userId is Guid guid ? guid : null);

        entity.AspNetUserId = fieldRedactor.RedactValue("AspNetUserId",
            auditEvent.AspNetUserId
            ?? (auditEvent.CustomFields.TryGetValue("AspNetUserId", out var aspNetUserId) ? aspNetUserId?.ToString() : null));

        if (auditEvent.CustomFields.TryGetValue("UserFullName", out var fullName))
            entity.UserFullName = fieldRedactor.RedactValue("UserFullName", fullName?.ToString());

        if (auditEvent.CustomFields.TryGetValue("TenantId", out var tenantId) && tenantId is Guid tenantGuid)
            entity.TenantId = tenantGuid;

        entity.EntityType = !string.IsNullOrEmpty(auditEvent.EntityName)
            ? auditEvent.EntityName
            : (auditEvent.CustomFields.TryGetValue("EntityType", out var entityType) ? entityType?.ToString() : null);

        entity.EntityId = fieldRedactor.RedactValue("EntityId",
            auditEvent.CustomFields.TryGetValue("EntityId", out var entityId) ? entityId?.ToString() : null);

        entity.Action = auditEvent.Action != AuditAction.Unknown
            ? auditEvent.Action.ToString()
            : (auditEvent.CustomFields.TryGetValue("Action", out var action) ? action?.ToString() : null);

        entity.CorrelationId = auditEvent.CorrelationId
            ?? (auditEvent.CustomFields.TryGetValue("CorrelationId", out var correlationId) ? correlationId?.ToString() : null);

        entity.IpAddress = SanitizeInput(fieldRedactor.RedactValue("IpAddress",
            auditEvent.IpAddress
            ?? (auditEvent.CustomFields.TryGetValue("IpAddress", out var ipAddress) ? ipAddress?.ToString() : null)));

        entity.UserAgent = SanitizeInput(fieldRedactor.RedactValue("UserAgent",
            auditEvent.UserAgent
            ?? (auditEvent.CustomFields.TryGetValue("UserAgent", out var userAgent) ? userAgent?.ToString() : null)));

        if (auditEvent.CustomFields.TryGetValue("RequestPath", out var requestPath))
            entity.RequestPath = SanitizeInput(fieldRedactor.RedactValue("RequestPath", requestPath?.ToString()));

        if (auditEvent.CustomFields.TryGetValue("RequestMethod", out var requestMethod))
            entity.RequestMethod = requestMethod?.ToString();

        // Redact CustomFields before serialization to prevent PHI/PII leaking into JsonData
        var redactedCustomFields = fieldRedactor.RedactFields(auditEvent.CustomFields);

        // Build additional data (unmapped custom fields) in a single pass
        Dictionary<string, object?>? additionalData = null;
        foreach (var field in redactedCustomFields)
        {
            if (IsFieldMapped(field.Key)) continue;
            additionalData ??= new();
            additionalData[field.Key] = field.Value;
        }

        // Redact target object before serialization to prevent PHI/PII leaking into JsonData
        var redactedTarget = fieldRedactor.RedactTarget(auditEvent.Target);

        // Serialize the entire event as JSON for JsonData field (uses redacted fields + target)
        entity.JsonData = JsonSerializer.Serialize(new
        {
            auditEvent.EventId,
            auditEvent.EventType,
            auditEvent.StartDate,
            auditEvent.EndDate,
            auditEvent.Duration,
            auditEvent.Environment,
            Target = redactedTarget,
            CustomFields = redactedCustomFields,
            auditEvent.Success,
            ErrorMessage = fieldRedactor.RedactValue("ErrorMessage", auditEvent.ErrorMessage)
        });

        // Store additional data (only serialize if non-empty)
        if (additionalData is { Count: > 0 })
        {
            entity.AdditionalData = JsonSerializer.Serialize(additionalData);
        }

        return entity;
    }

    /// <summary>
    /// Standard mapped field names for O(1) lookup
    /// </summary>
    private static readonly HashSet<string> _mappedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserId", "AspNetUserId", "UserFullName", "TenantId", "EntityType",
        "EntityId", "Action", "CorrelationId", "IpAddress", "UserAgent",
        "RequestPath", "RequestMethod", "Environment"
    };

    /// <summary>
    /// Is the specified field name one of the standard mapped fields?
    /// </summary>
    private static bool IsFieldMapped(string fieldName) => _mappedFields.Contains(fieldName);

    /// <summary>
    /// Strips control characters (newlines, tabs, etc.) from user-controlled input
    /// to prevent log injection attacks where attackers embed forged log entries
    /// via HTTP headers like User-Agent.
    /// </summary>
    private static string? SanitizeInput(string? value)
    {
        if (value is null) return null;

        // Fast path: zero-allocation scan for control characters
        bool hasControlChars = false;
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsControl(value[i]))
            {
                hasControlChars = true;
                break;
            }
        }

        if (!hasControlChars) return value;

        return string.Create(value.Length, value, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++)
                span[i] = char.IsControl(src[i]) ? ' ' : src[i];
        });
    }
}
