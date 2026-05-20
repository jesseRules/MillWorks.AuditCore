using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Sinks.Writers;

/// <summary>
/// Batch writer for explicit-event envelopes. Maps envelopes to <see cref="AuditEvent"/>
/// and delegates to <see cref="IAuditLogger"/> for persistence, returning per-envelope outcomes.
/// </summary>
internal sealed class AuditEventBatchWriter(
    IAuditLogger auditLogger,
    ILogger<AuditEventBatchWriter> logger) : IAuditEventBatchWriter
{
    public async Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        if (envelopes.Count == 0)
            return [];

        var auditEvents = new List<AuditEvent>(envelopes.Count);
        var envelopeByEventIndex = new List<AuditEnvelope>(envelopes.Count);

        foreach (var envelope in envelopes)
        {
            if (envelope is null)
                continue;

            auditEvents.Add(MapToAuditEvent(envelope));
            envelopeByEventIndex.Add(envelope);
        }

        if (auditEvents.Count == 0)
            return [];

        var batchResult = await auditLogger.LogBatchAsync(auditEvents, cancellationToken);

        var outcomes = new List<WriteOutcome>(envelopeByEventIndex.Count);

        if (batchResult.Success)
        {
            if (batchResult.IsDuplicate)
            {
                logger.LogDebug(
                    "Duplicate key detected for {EventCount} explicit event(s), treating as success",
                    auditEvents.Count);

                foreach (var envelope in envelopeByEventIndex)
                {
                    outcomes.Add(WriteOutcome.Duplicate(envelope.EnvelopeId));
                }
            }
            else
            {
                logger.LogDebug("Wrote {EventCount} explicit event(s)", auditEvents.Count);

                foreach (var envelope in envelopeByEventIndex)
                {
                    outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
                }
            }
        }
        else
        {
            logger.LogWarning(batchResult.Exception, "Failed to write {EventCount} explicit event(s)", auditEvents.Count);

            var errorMessage = batchResult.Exception?.Message ?? "Batch write failed";

            // If FailedEvents is empty, treat it as total failure (all events failed).
            // This handles cases like connection failures where no per-event details exist.
            if (batchResult.FailedEvents.Count == 0)
            {
                foreach (var envelope in envelopeByEventIndex)
                {
                    outcomes.Add(WriteOutcome.Failed(envelope.EnvelopeId, errorMessage, isRetryable: true, batchResult.Exception));
                }
            }
            else
            {
                // Partial failure: correlate failed events by EventId.
                // Assumes FailedEvents contains the same AuditEvent instances (or matching EventIds)
                // that were passed to LogBatchAsync.
                var failedEventIds = batchResult.FailedEvents
                    .Select(static e => e.EventId)
                    .ToHashSet();

                for (var i = 0; i < envelopeByEventIndex.Count; i++)
                {
                    var envelope = envelopeByEventIndex[i];
                    var auditEvent = auditEvents[i];

                    if (failedEventIds.Contains(auditEvent.EventId))
                    {
                        outcomes.Add(WriteOutcome.Failed(envelope.EnvelopeId, errorMessage, isRetryable: true, batchResult.Exception));
                    }
                    else
                    {
                        outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
                    }
                }
            }
        }

        return outcomes;
    }

    private static AuditEvent MapToAuditEvent(AuditEnvelope envelope)
    {
        var auditEvent = new AuditEvent
        {
            // Use EnvelopeId as EventId to ensure idempotency: replaying the same
            // envelope produces the same EventId, which the PK constraint catches.
            EventId = envelope.EnvelopeId,
            EventType = envelope.EventType ?? string.Empty,
            EntityName = envelope.EntityName,
            Action = envelope.Action,
            StartDate = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            IpAddress = envelope.IpAddress,
            UserAgent = envelope.UserAgent,
            AspNetUserId = envelope.UserId
        };

        if (envelope.EntityId is { } entityId)
        {
            auditEvent.KeyValues["Id"] = entityId;
        }

        if (envelope.Description is { } description)
        {
            auditEvent.CustomFields["Description"] = description;
        }

        if (envelope.AdditionalData is { } additionalData)
        {
            auditEvent.CustomFields["AdditionalData"] = additionalData;
        }

        return auditEvent;
    }
}
