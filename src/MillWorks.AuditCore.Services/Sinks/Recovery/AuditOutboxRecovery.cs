using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal sealed class AuditOutboxRecovery(OutboxStateStore state, IServiceScopeFactory scopes,
    IAuditOutboxRecoveryAuthorization? authorization = null) : IAuditOutboxRecovery
{
    private async ValueTask<IAuditOutboxRecoveryPermit> PermitAsync(Guid id, Guid? tenant, Guid? cycle,
        AuditOutboxRecoveryAction action, CancellationToken ct)
    {
        if (id == Guid.Empty || tenant == Guid.Empty) throw new ArgumentException("Invalid recovery identity.");
        if (authorization is null) throw new UnauthorizedAccessException("Outbox recovery requires a host authorization policy.");
        var permit = await authorization.AcquireAsync(new(id, tenant, cycle), action, ct);
        if (permit is null) throw new UnauthorizedAccessException("Outbox recovery authorization denied.");
        if (permit.TenantId != tenant || string.IsNullOrWhiteSpace(permit.ActorId) || permit.ActorId.Length > 256)
        {
            await permit.DisposeAsync();
            throw new UnauthorizedAccessException("Invalid outbox recovery permit scope.");
        }
        try { await permit.CheckCurrentAsync(ct); return permit; }
        catch { await permit.DisposeAsync(); throw; }
    }

    public async Task<AuditOutboxFailureSummary?> InspectAsync(Guid rowId, Guid? tenantId, CancellationToken cancellationToken = default)
    {
        await using var permit = await PermitAsync(rowId, tenantId, null, AuditOutboxRecoveryAction.Inspect, cancellationToken);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await db.AuditOutbox.AsNoTracking().SingleOrDefaultAsync(x => x.Id == rowId &&
            x.FailureTenantId == tenantId && x.FailureCycleId != null, cancellationToken);
        if (row is null) return null;
        await permit.CheckCurrentAsync(cancellationToken);
        var pending = await db.AuditOutboxFailureHandoffs.CountAsync(x => x.OutboxRowId == rowId &&
            (x.Status == AuditOutboxHandoffStatus.Pending || x.Status == AuditOutboxHandoffStatus.Dispatching), cancellationToken);
        var delivered = await db.AuditOutboxFailureHandoffs.CountAsync(x => x.OutboxRowId == rowId && x.Status == AuditOutboxHandoffStatus.Delivered, cancellationToken);
        return new(row.Id, row.FailureTenantId, row.FailureCycleId!.Value, row.AttemptCount,
            row.LastError ?? "write_failed", row.ResolutionId is not null, pending, delivered);
    }

    public Task<AuditOutboxResolutionResult> ReplayAsync(Guid resolutionId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        string reason, CancellationToken cancellationToken = default) => ResolveAsync(resolutionId, rowId, tenantId, failureCycleId, reason, true, cancellationToken);
    public Task<AuditOutboxResolutionResult> SkipAsync(Guid resolutionId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        string reason, CancellationToken cancellationToken = default) => ResolveAsync(resolutionId, rowId, tenantId, failureCycleId, reason, false, cancellationToken);

    private async Task<AuditOutboxResolutionResult> ResolveAsync(Guid operation, Guid rowId, Guid? tenantId, Guid cycle,
        string reason, bool replay, CancellationToken ct)
    {
        if (cycle == Guid.Empty || string.IsNullOrWhiteSpace(reason) || reason.Length > 2000)
            throw new ArgumentException("Resolution requires an observed cycle and bounded reason.");
        await using var permit = await PermitAsync(rowId, tenantId, cycle,
            replay ? AuditOutboxRecoveryAction.Replay : AuditOutboxRecoveryAction.Skip, ct);
        var result = await state.ResolveAsync(operation, rowId, tenantId, cycle, SensitiveContentSanitizer.Sanitize(reason, 2000),
            replay, permit, ct);
        return new(operation, result.Applied.Length == 1, result.ExternalDeliveryMayBeInFlight);
    }

    public async Task<bool> CleanupAsync(Guid operationId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        CancellationToken cancellationToken = default)
    {
        await using var permit = await PermitAsync(rowId, tenantId, failureCycleId, AuditOutboxRecoveryAction.Cleanup, cancellationToken);
        var result = await state.CleanupAsync(operationId, rowId, tenantId, failureCycleId, permit, cancellationToken);
        return result.Applied.Length == 1;
    }
}
