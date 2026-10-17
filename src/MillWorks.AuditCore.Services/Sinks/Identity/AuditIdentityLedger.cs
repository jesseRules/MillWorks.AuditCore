using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Sinks.Identity;

/// <summary>Stages comparison evidence on the SAME context as the accepted write. The caller
/// must authorize the publication before invoking this internal persistence boundary.
/// A unique database key arbitrates competing reservations; staging is never durable success.</summary>
internal static class AuditIdentityLedger
{
    internal static Guid LogicalId(AuditEnvelope e) => e.Kind == Abstractions.Enums.AuditEnvelopeKind.ExplicitEvent
        ? e.ExplicitEventId!.Value : e.EnvelopeId;

    internal static async Task<HashSet<Guid>> StageAsync(DbContext db, string channel, IReadOnlyList<AuditEnvelope> envelopes, CancellationToken ct)
    {
        if (db.Model.FindEntityType(typeof(AuditEnvelopeIdentity)) is null)
            throw new InvalidOperationException("The consumer model must map AuditEnvelopeIdentity before enabling H3a.");
        var set = db.Set<AuditEnvelopeIdentity>();
        var ids = envelopes.Select(LogicalId).Distinct().ToArray();
        var known = new Dictionary<Guid, AuditEnvelopeIdentity>();
        foreach (var chunk in ids.Chunk(500))
            foreach (var receipt in await set.AsNoTracking().Where(x => x.Channel == channel && chunk.Contains(x.LogicalId)).ToListAsync(ct))
                known.Add(receipt.LogicalId, receipt);
        var durableReceiptIds = known.Keys.ToHashSet();
        var sourceIds = new HashSet<Guid>();
        foreach (var chunk in ids.Chunk(500))
        {
            var present = channel switch
            {
                "event" => await db.Set<AuditEventEntity>().AsNoTracking().Where(x => chunk.Contains(x.EventId)).Select(x => x.EventId).ToListAsync(ct),
                "entity" => await db.Set<AuditLogEntity>().AsNoTracking().Where(x => x.EnvelopeId.HasValue && chunk.Contains(x.EnvelopeId.Value)).Select(x => x.EnvelopeId!.Value).ToListAsync(ct),
                "outbox" => await db.Set<AuditOutboxEntity>().AsNoTracking().Where(x => chunk.Contains(x.IdempotencyKey)).Select(x => x.IdempotencyKey).ToListAsync(ct),
                _ => throw new ArgumentException("Unknown identity channel.", nameof(channel))
            };
            sourceIds.UnionWith(present);
        }
        // At READ COMMITTED/RCSI a winner can commit its receipt + source after our
        // first receipt read. Recheck only that ambiguous set before classifying it
        // as historical data. Evidence is never inferred from source existence alone.
        foreach (var chunk in sourceIds.Where(id => !known.ContainsKey(id)).Chunk(500))
        {
            foreach (var receipt in await set.AsNoTracking()
                         .Where(x => x.Channel == channel && chunk.Contains(x.LogicalId)).ToListAsync(ct))
            {
                known.Add(receipt.LogicalId, receipt);
                durableReceiptIds.Add(receipt.LogicalId);
            }
        }
        foreach (var entry in db.ChangeTracker.Entries<AuditEnvelopeIdentity>()
                     .Where(x => x.State == EntityState.Added && x.Entity.Channel == channel))
            known.TryAdd(entry.Entity.LogicalId, entry.Entity);
        if (channel == "outbox")
            sourceIds.UnionWith(db.ChangeTracker.Entries<AuditOutboxEntity>()
                .Where(x => x.State is not EntityState.Deleted and not EntityState.Detached).Select(x => x.Entity.IdempotencyKey));
        var staged = new List<AuditEnvelopeIdentity>();
        var conflicts = new HashSet<Guid>();
        var unverified = new HashSet<Guid>();
        foreach (var envelope in envelopes)
        {
            AuditEnvelopeContent.Validate(envelope);
            var id = LogicalId(envelope);
            var fingerprint = AuditEnvelopeContent.Fingerprint(envelope);
            if (known.TryGetValue(id, out var receipt))
            {
                if (receipt.ContentVersion != AuditEnvelopeContent.Version ||
                    (durableReceiptIds.Contains(id) && !sourceIds.Contains(id))) unverified.Add(id);
                else if (receipt.Kind != envelope.Kind || receipt.TenantId != envelope.GovernanceIdentity?.TenantId ||
                         !string.Equals(receipt.Fingerprint, fingerprint, StringComparison.Ordinal)) conflicts.Add(id);
                continue;
            }
            if (sourceIds.Contains(id)) { unverified.Add(id); continue; }
            receipt = new AuditEnvelopeIdentity
            {
                Channel = channel, LogicalId = id, Kind = envelope.Kind,
                TenantId = envelope.GovernanceIdentity?.TenantId, ContentVersion = AuditEnvelopeContent.Version,
                Fingerprint = fingerprint, AcceptedAt = DateTimeOffset.UtcNow
            };
            known.Add(id, receipt);
            staged.Add(receipt);
        }
        // Validate the full input before attaching any receipt, including tracked/same-batch collisions.
        if (conflicts.Count > 0) throw new AuditIdentityConflictException(conflicts.ToArray());
        if (unverified.Count > 0) throw new AuditIdentityConflictException(unverified.ToArray(), unverified: true);
        set.AddRange(staged);
        return durableReceiptIds;
    }

    internal static async Task VerifyDurableAsync(DbContext db, string channel, AuditEnvelope envelope, CancellationToken ct)
    {
        var id = LogicalId(envelope);
        var receipt = await db.Set<AuditEnvelopeIdentity>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Channel == channel && x.LogicalId == id, ct);
        if (receipt is null || receipt.ContentVersion != AuditEnvelopeContent.Version)
            throw new AuditIdentityConflictException([id], unverified: true);
        var sourceExists = channel switch
        {
            "event" => await db.Set<AuditEventEntity>().AsNoTracking().AnyAsync(x => x.EventId == id, ct),
            "entity" => await db.Set<AuditLogEntity>().AsNoTracking().AnyAsync(x => x.EnvelopeId == id, ct),
            "outbox" => await db.Set<AuditOutboxEntity>().AsNoTracking().AnyAsync(x => x.IdempotencyKey == id, ct),
            _ => false
        };
        if (!sourceExists) throw new AuditIdentityConflictException([id], unverified: true);
        if (receipt.Kind != envelope.Kind || receipt.TenantId != envelope.GovernanceIdentity?.TenantId ||
            receipt.Fingerprint != AuditEnvelopeContent.Fingerprint(envelope))
            throw new AuditIdentityConflictException([id]);
    }
}
