using Microsoft.Extensions.Logging;
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
        Identity.AuditEnvelopeContent.ValidateBatch(envelopes);
        if (envelopes.Count == 0) return [];
        var fingerprints = envelopes.ToDictionary(e => e.EnvelopeId, Identity.AuditEnvelopeContent.Fingerprint);
        var outcomes = new Dictionary<Guid, WriteOutcome>();
        var groups = envelopes.GroupBy(e => e.ExplicitEventId!.Value).ToDictionary(g => g.Key, g => g.ToList());
        if (auditLogger is not IAuditEnvelopeBatchLogger envelopeLogger)
            throw new InvalidOperationException("The configured audit logger must implement IAuditEnvelopeBatchLogger.");
        var pending = new Dictionary<Guid, AuditEnvelope>();
        foreach (var (id, group) in groups)
        {
            var candidate = group[0];
            pending.Add(id, candidate);
            foreach (var reference in group.Skip(1))
                if (fingerprints[reference.EnvelopeId] != fingerprints[candidate.EnvelopeId])
                    outcomes.Add(reference.EnvelopeId, WriteOutcome.Conflict(reference.EnvelopeId,
                        new Identity.AuditIdentityConflictException([id])));
        }
        while (pending.Count > 0)
        {
            BatchAuditResult result;
            try { result = await envelopeLogger.LogEnvelopeBatchAsync(pending.Values.ToList(), cancellationToken); }
            catch (Identity.AuditIdentityConflictException conflict)
            {
                var affected = conflict.LogicalIds.Where(pending.ContainsKey).ToArray();
                if (affected.Length == 0) throw;
                foreach (var id in affected)
                {
                    foreach (var input in groups[id].Where(e => !outcomes.ContainsKey(e.EnvelopeId)))
                        outcomes.Add(input.EnvelopeId, WriteOutcome.Conflict(input.EnvelopeId, conflict));
                    pending.Remove(id);
                }
                continue;
            }
            var exact = ValidateResult(result, pending.Keys);
            foreach (var (id, candidate) in pending)
            {
                var item = exact[id];
                foreach (var input in groups[id].Where(e => !outcomes.ContainsKey(e.EnvelopeId)))
                {
                    var outcome = item.Status == AuditEventWriteStatus.Failed && item.Exception is Identity.AuditIdentityConflictException conflict
                        ? WriteOutcome.Conflict(input.EnvelopeId, conflict)
                        : item.Status == AuditEventWriteStatus.Failed
                        ? WriteOutcome.Failed(input.EnvelopeId, item.Exception?.Message ?? "Batch write failed", item.IsRetryable, item.Exception)
                        : item.Status == AuditEventWriteStatus.Duplicate || input != groups[id][0]
                            ? WriteOutcome.Duplicate(input.EnvelopeId) : WriteOutcome.Success(input.EnvelopeId);
                    outcomes.Add(input.EnvelopeId, outcome);
                }
            }
            pending.Clear();
        }
        logger.LogDebug("Classified {EnvelopeCount} explicit envelope outcome(s)", envelopes.Count);
        return envelopes.Select(e => outcomes[e.EnvelopeId]).ToList();
    }

    internal static Dictionary<Guid, AuditEventOutcome> ValidateResult(BatchAuditResult result, IEnumerable<Guid> expected)
    {
        var ids = expected.ToHashSet();
        var items = result?.EventOutcomes;
        if (result is null || (result.Success && result.Exception is not null) || result.FailedEvents is null || result.FailedEvents.Any(x => x is null) || items is null || items.Count != ids.Count || result.EventCount != ids.Count ||
            items.Any(x => x is null || !ids.Contains(x.EventId) || !Enum.IsDefined(x.Status) ||
                (x.Status != AuditEventWriteStatus.Failed && (x.Exception is not null || x.IsRetryable))) ||
            items.Select(x => x.EventId).Distinct().Count() != ids.Count ||
            result.Success != items.All(x => x.Status != AuditEventWriteStatus.Failed) ||
            result.IsDuplicate != (items.Count > 0 && items.All(x => x.Status == AuditEventWriteStatus.Duplicate)) ||
            !result.FailedEvents.Select(x => x.EventId).Order().SequenceEqual(
                items.Where(x => x.Status == AuditEventWriteStatus.Failed).Select(x => x.EventId).Order()))
            throw new Processing.AuditBatchContractException("Envelope logger must return exactly one consistent outcome per logical event.");
        return items.ToDictionary(x => x.EventId);
    }

    internal static AuditEvent MapToAuditEvent(AuditEnvelope envelope)
    {
        var auditEvent = new AuditEvent
        {
            SourceEnvelope = envelope,
            EventId = envelope.ExplicitEventId!.Value,
            EventType = envelope.EventType ?? string.Empty,
            EntityName = envelope.EntityName,
            Action = envelope.Action,
            StartDate = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            IpAddress = envelope.IpAddress,
            UserAgent = envelope.UserAgent,
            AspNetUserId = envelope.UserId
        };

        if (envelope.GovernanceIdentity is { } governance)
        {
            auditEvent.CustomFields["TenantId"] = governance.TenantId;
            auditEvent.CustomFields["AuditGovernanceIdentity"] = governance;
        }
        if (envelope.SubjectReferences is { Count: > 0 })
            auditEvent.CustomFields["AuditSubjectReferences"] = envelope.SubjectReferences;

        if (envelope.EntityId is { } entityId)
        {
            auditEvent.KeyValues["Id"] = entityId;
            // AuditLogger indexes EntityId from CustomFields; KeyValues only enters the payload.
            auditEvent.CustomFields["EntityId"] = entityId.ToString("D");
        }

        if (envelope.EntityId is null && envelope.EntityIdString is { } entityIdString)
        {
            auditEvent.KeyValues["Id"] = entityIdString;
            auditEvent.CustomFields["EntityId"] = entityIdString;
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
