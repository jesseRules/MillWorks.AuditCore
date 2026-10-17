using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks.Identity;

namespace MillWorks.AuditCore.Services.Core;

public partial class AuditLogger : Interfaces.IAuditEnvelopeBatchLogger
{
    public Task<BatchAuditResult> LogEnvelopeBatchAsync(IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        AuditEnvelopeContent.ValidateBatch(envelopes);
        if (envelopes.Any(e => e.Kind != Abstractions.Enums.AuditEnvelopeKind.ExplicitEvent))
            throw new ArgumentException("Only explicit events are accepted by the envelope logger.", nameof(envelopes));
        return LogBatchAsync(envelopes.Select(Sinks.Writers.AuditEventBatchWriter.MapToAuditEvent).ToList(), cancellationToken);
    }

    private static BatchAuditResult CompletedBatch(IReadOnlyList<AuditEvent> events, HashSet<Guid> duplicates,
        IReadOnlyDictionary<Guid, AuditIdentityConflictException>? conflicts = null) => new()
    {
        Success = conflicts is null || conflicts.Count == 0,
        EventCount = events.Count,
        IsDuplicate = events.Count > 0 && events.All(e => duplicates.Contains(e.EventId)) && (conflicts is null || conflicts.Count == 0),
        FailedEvents = events.Where(e => conflicts?.ContainsKey(e.EventId) == true).ToArray(),
        Exception = conflicts?.Values.FirstOrDefault(),
        EventOutcomes = events.All(e => e.SourceEnvelope is not null)
            ? events.Select(e => conflicts is not null && conflicts.TryGetValue(e.EventId, out var conflict)
                ? new AuditEventOutcome(e.EventId, AuditEventWriteStatus.Failed, conflict)
                : new AuditEventOutcome(e.EventId, duplicates.Contains(e.EventId)
                    ? AuditEventWriteStatus.Duplicate : AuditEventWriteStatus.Persisted)).ToArray() : null
    };

    private static AuditEvent RestoreEnvelopeEvent(AuditEvent e)
    {
        if (e.SourceEnvelope is not { } envelope) return e;
        AuditEnvelopeContent.Validate(envelope);
        if (envelope.Kind != Abstractions.Enums.AuditEnvelopeKind.ExplicitEvent || e.EventId != envelope.ExplicitEventId)
            throw new ArgumentException("Serialized event and envelope identity must agree.");
        return Sinks.Writers.AuditEventBatchWriter.MapToAuditEvent(envelope);
    }

    private async Task<HashSet<Guid>> StageEnvelopeIdentitiesAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        // A direct logger call cannot use its legacy duplicate behavior to bypass an identity
        // already owned by the envelope protocol. It must carry the original envelope context.
        foreach (var chunk in events.Where(e => e.SourceEnvelope is null).Select(e => e.EventId).Distinct().Chunk(500))
        {
            var owned = await dbContext.Set<AuditEnvelopeIdentity>().AsNoTracking()
                .Where(x => x.Channel == "event" && chunk.Contains(x.LogicalId)).Select(x => x.LogicalId).ToListAsync(ct);
            if (owned.Count > 0) throw new AuditIdentityConflictException(owned, unverified: true);
        }
        var envelopes = events.Where(e => e.SourceEnvelope is not null).Select(e => e.SourceEnvelope!).ToList();
        return envelopes.Count > 0 ? await AuditIdentityLedger.StageAsync(dbContext, "event", envelopes, ct) : [];
    }

    private async Task VerifyEnvelopeDuplicatesAsync(IReadOnlyList<AuditEvent> events, CancellationToken ct)
    {
        foreach (var e in events)
        {
            if (e.SourceEnvelope is not null)
                await AuditIdentityLedger.VerifyDurableAsync(dbContext, "event", e.SourceEnvelope, ct);
            else
                await StageEnvelopeIdentitiesAsync([e], ct);
        }
    }
}
