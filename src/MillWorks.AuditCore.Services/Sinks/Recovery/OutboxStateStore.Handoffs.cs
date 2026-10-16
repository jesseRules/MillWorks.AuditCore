using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Entities;

#pragma warning disable EF1002 // Delimited EF model identifiers only; values are parameters.

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal sealed partial class OutboxStateStore
{
    public Task<OutboxOperationResult> ClaimHandoffsAsync(Guid operation, int size, TimeSpan duration, CancellationToken ct)
    {
        BatchSize(size);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var token = operation.ToString("N");
        DateTimeOffset? asOf = null;
        return ExecuteAsync(operation, "handoff_claim", new { size, duration }, async (db, cancel) =>
        {
            var now = asOf ??= await NowAsync(db, cancel);
            // Bounded recovery has one time authority/as-of value and never changes attempt count.
            var expired = await LockedHandoffs(db).Where(x => x.Status == AuditOutboxHandoffStatus.Dispatching && x.LeaseExpiresAt <= now)
                .OrderBy(x => x.LeaseExpiresAt).ThenBy(x => x.Id).Take(size).Select(x => x.Id).ToArrayAsync(cancel);
            if (expired.Length != 0)
            {
                var recovered = await db.AuditOutboxFailureHandoffs.Where(x => expired.Contains(x.Id) && x.Status == AuditOutboxHandoffStatus.Dispatching && x.LeaseExpiresAt <= now)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AuditOutboxHandoffStatus.Pending)
                        .SetProperty(x => x.ClaimToken, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), cancel);
                if (recovered != expired.Length) throw new DBConcurrencyException("Handoff recovery changed; transaction rolled back.");
            }
            var query = db.Database.IsSqlServer()
                ? db.AuditOutboxFailureHandoffs.FromSqlRaw($"SELECT * FROM {Table<AuditOutboxFailureHandoff>(db)} WITH (UPDLOCK, READPAST, READCOMMITTEDLOCK)").AsNoTracking()
                : db.AuditOutboxFailureHandoffs.AsNoTracking();
            var candidates = await query.Where(x => x.Status == AuditOutboxHandoffStatus.Pending &&
                    (x.NextRetryAt == null || x.NextRetryAt <= now))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(size).ToArrayAsync(cancel);
            var ids = candidates.Select(x => x.Id).ToArray();
            if (ids.Length != 0)
            {
                var count = await db.AuditOutboxFailureHandoffs.Where(x => ids.Contains(x.Id) && x.Status == AuditOutboxHandoffStatus.Pending &&
                        (x.NextRetryAt == null || x.NextRetryAt <= now))
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, AuditOutboxHandoffStatus.Dispatching)
                        .SetProperty(x => x.ClaimToken, token).SetProperty(x => x.LeaseExpiresAt, now + duration), cancel);
                if (count != ids.Length) throw new DBConcurrencyException("Handoff eligibility changed; transaction rolled back.");
            }
            return new(operation, ids.Concat(expired).Distinct().ToArray(), [], [], candidates.Select(x => new HandoffClaim(x.Id, x.OutboxRowId,
                token, x.EnvelopeJson, x.ReasonCode, x.AttemptCount)).ToArray());
        }, ct);
    }

    internal async Task<bool> OwnsHandoffAsync(HandoffClaim claim, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<EntityFramework.Data.AuditDbContext>();
        return await db.AuditOutboxFailureHandoffs.AsNoTracking().AnyAsync(x => x.Id == claim.Id &&
            x.Status == AuditOutboxHandoffStatus.Dispatching && x.ClaimToken == claim.Token, ct);
    }

    public Task<OutboxOperationResult> AcknowledgeHandoffAsync(Guid operation, HandoffClaim claim, bool delivered,
        CancellationToken ct) => ExecuteAsync(operation, "handoff_ack", new { claim.Id, claim.Token, delivered }, async (db, cancel) =>
    {
        var now = await NowAsync(db, cancel);
        var owned = await LockedHandoffs(db).SingleOrDefaultAsync(x => x.Id == claim.Id &&
            x.Status == AuditOutboxHandoffStatus.Dispatching && x.ClaimToken == claim.Token, cancel);
        if (owned is null) return new(operation, [], [claim.Id], [], []);
        var delay = TimeSpan.FromSeconds(Math.Min(3600, 5 * Math.Pow(2, Math.Min(owned.AttemptCount, 10))));
        var count = await db.AuditOutboxFailureHandoffs.Where(x => x.Id == claim.Id &&
                x.Status == AuditOutboxHandoffStatus.Dispatching && x.ClaimToken == claim.Token)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, delivered ? AuditOutboxHandoffStatus.Delivered : AuditOutboxHandoffStatus.Pending)
                .SetProperty(x => x.ClaimToken, (string?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(x => x.AttemptCount, x => x.AttemptCount + 1)
                .SetProperty(x => x.CompletedAt, delivered ? now : (DateTimeOffset?)null)
                .SetProperty(x => x.NextRetryAt, delivered ? (DateTimeOffset?)null : now + delay), cancel);
        if (count != 1) throw new DBConcurrencyException("Handoff acknowledgement changed; transaction rolled back.");
        return new(operation, [claim.Id], [], [], []);
    }, ct);
}
