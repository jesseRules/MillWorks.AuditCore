using System.Data;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal sealed partial class OutboxStateStore
{
    public Task<OutboxOperationResult> ResolveAsync(Guid operation, Guid id, Guid? tenant, Guid cycle, string reason,
        bool replay, IAuditOutboxRecoveryPermit permit, CancellationToken ct) =>
        ExecuteAsync(operation, "resolve", new { id, tenant, cycle, reason, replay, permit.ActorId }, async (db, cancel) =>
        {
            await permit.CheckCurrentAsync(cancel);
            var row = await LockedRows(db).SingleOrDefaultAsync(x => x.Id == id && x.Status == AuditOutboxStatus.Failed &&
                x.FailureTenantId == tenant && x.FailureCycleId == cycle && x.ResolutionId == null, cancel);
            if (row is null) return new(operation, [], [id], [], []);
            if (replay) QueuedEnvelopeValidator.Read(row.EnvelopeJson, row.EnvelopeVersion);
            var now = await NowAsync(db, cancel);
            var projections = await LockedHandoffs(db).Where(x => x.OutboxRowId == id && x.FailureCycleId == cycle &&
                (x.Status == AuditOutboxHandoffStatus.Pending || x.Status == AuditOutboxHandoffStatus.Dispatching)).ToListAsync(cancel);
            await permit.CheckCurrentAsync(cancel);
            var changed = await db.AuditOutbox.Where(x => x.Id == id && x.Status == AuditOutboxStatus.Failed && x.FailureCycleId == cycle && x.ResolutionId == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, replay ? AuditOutboxStatus.Pending : AuditOutboxStatus.Failed)
                    .SetProperty(x => x.ResolutionId, (Guid?)operation).SetProperty(x => x.AttemptCount, replay ? 0 : row.AttemptCount)
                    .SetProperty(x => x.NextRetryAt, (DateTimeOffset?)null), cancel);
            if (changed != 1) throw new DBConcurrencyException("Operator resolution changed; transaction rolled back.");
            var projectionIds = projections.Select(x => x.Id).ToArray();
            if (projectionIds.Length != 0)
            {
                var suppressed = await db.AuditOutboxFailureHandoffs.Where(x => projectionIds.Contains(x.Id) &&
                        (x.Status == AuditOutboxHandoffStatus.Pending || x.Status == AuditOutboxHandoffStatus.Dispatching))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AuditOutboxHandoffStatus.Suppressed)
                        .SetProperty(x => x.ClaimToken, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                        .SetProperty(x => x.CompletedAt, now), cancel);
                if (suppressed != projectionIds.Length) throw new DBConcurrencyException("Projection suppression changed; transaction rolled back.");
            }
            db.AuditOutboxResolutions.Add(new AuditOutboxResolution
            {
                Id = operation, OutboxRowId = id, FailureCycleId = cycle, TenantId = tenant,
                ActorId = permit.ActorId, Action = replay ? "Replay" : "Skip", Reason = reason, CreatedAt = now
            });
            return new(operation, [id], [], [], [], projections.Any(x => x.Status == AuditOutboxHandoffStatus.Dispatching));
        }, ct, permit);

    public Task<OutboxOperationResult> CleanupAsync(Guid operation, Guid id, Guid? tenant, Guid cycle,
        IAuditOutboxRecoveryPermit permit, CancellationToken ct) =>
        ExecuteAsync(operation, "cleanup", new { id, tenant, cycle, permit.ActorId }, async (db, cancel) =>
        {
            await permit.CheckCurrentAsync(cancel);
            var now = await NowAsync(db, cancel);
            var row = await LockedRows(db).SingleOrDefaultAsync(x => x.Id == id && x.FailureTenantId == tenant &&
                ((x.FailureCycleId == cycle && x.ResolutionId != null && x.Status == AuditOutboxStatus.Failed) ||
                 x.Status == AuditOutboxStatus.Completed), cancel);
            if (row is null) return new(operation, [], [id], [], []);
            var resolution = await db.AuditOutboxResolutions.AsNoTracking().Where(x => x.OutboxRowId == id && x.FailureCycleId == cycle)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(cancel);
            if (resolution is null || resolution.CreatedAt > now - VerificationRetention ||
                (row.Status == AuditOutboxStatus.Completed && (row.CompletedAt == null || row.CompletedAt > now - VerificationRetention)))
                return new(operation, [], [id], [], []);
            var projections = await LockedHandoffs(db).Where(x => x.OutboxRowId == id).ToListAsync(cancel);
            if (projections.Any(x => x.Status is AuditOutboxHandoffStatus.Pending or AuditOutboxHandoffStatus.Dispatching ||
                x.CompletedAt == null || x.CompletedAt > now - VerificationRetention)) return new(operation, [], [id], [], []);
            // Receipts contain metadata only, never copied envelope payloads. Keep their identities
            // indefinitely; payload removal cannot erase needed commit-verification evidence.
            var identity = id.ToString();
            if (await db.AuditOutboxOperations.AnyAsync(x => x.VerifyUntil > now && x.ResultJson.Contains(identity), cancel))
                return new(operation, [], [id], [], []);
            await permit.CheckCurrentAsync(cancel);
            var removedProjections = await db.AuditOutboxFailureHandoffs.Where(x => x.OutboxRowId == id).ExecuteDeleteAsync(cancel);
            var removedSource = await db.AuditOutbox.Where(x => x.Id == id && x.Status == row.Status && x.ResolutionId == row.ResolutionId)
                .ExecuteDeleteAsync(cancel);
            if (removedSource != 1 || removedProjections != projections.Count)
                throw new DBConcurrencyException("Authorized cleanup changed; transaction rolled back.");
            db.AuditOutboxResolutions.Add(new AuditOutboxResolution
            {
                Id = operation, OutboxRowId = id, FailureCycleId = cycle, TenantId = tenant,
                ActorId = permit.ActorId, Action = "Cleanup", CreatedAt = now,
                Reason = $"Authorized payload cleanup following resolution {resolution.Id}."
            });
            return new(operation, [id], [], [], []);
        }, ct, permit);
    public Task<OutboxOperationResult> CleanupCompletedAsync(Guid operation, Guid id, Guid tenant,
        DateTimeOffset completedBefore, IAuditOutboxRecoveryPermit permit, CancellationToken ct) =>
        ExecuteAsync(operation, "cleanup_completed", new { id, tenant, completedBefore, permit.ActorId }, async (db, cancel) =>
        {
            await permit.CheckCurrentAsync(cancel);
            var now = await NowAsync(db, cancel);
            var row = await LockedRows(db).SingleOrDefaultAsync(x => x.Id == id &&
                x.Status == AuditOutboxStatus.Completed && x.FailureCycleId == null && x.ResolutionId == null &&
                x.CompletedAt != null && x.CompletedAt < completedBefore &&
                x.CompletedAt <= now - VerificationRetention, cancel);
            if (row is null) return new(operation, [], [id], [], []);
            // Keep the immutable acceptance receipt. Missing/unknown tenant evidence is not
            // permission to delete a payload, and projections/resolutions use the other path.
            if (!await db.Set<AuditEnvelopeIdentity>().AnyAsync(x => x.Channel == "outbox" &&
                    x.LogicalId == row.IdempotencyKey && x.TenantId == tenant, cancel) ||
                await db.AuditOutboxFailureHandoffs.AnyAsync(x => x.OutboxRowId == id, cancel) ||
                await db.AuditOutboxResolutions.AnyAsync(x => x.OutboxRowId == id, cancel) ||
                await db.AuditOutboxOperations.AnyAsync(x => x.VerifyUntil > now && x.ResultJson.Contains(id.ToString()), cancel))
                return new(operation, [], [id], [], []);
            await permit.CheckCurrentAsync(cancel);
            var removed = await db.AuditOutbox.Where(x => x.Id == id && x.Status == AuditOutboxStatus.Completed &&
                    x.FailureCycleId == null && x.ResolutionId == null && x.CompletedAt == row.CompletedAt)
                .ExecuteDeleteAsync(cancel);
            if (removed != 1) throw new DBConcurrencyException("Completed outbox cleanup changed; transaction rolled back.");
            return new(operation, [id], [], [], []);
        }, ct, permit);

}
