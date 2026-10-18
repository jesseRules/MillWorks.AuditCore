namespace MillWorks.AuditCore.Services.Sinks.Recovery;

/// <summary>AuditCore-owned persistence seam. Every mutation retains its operation identity,
/// exact applied/rejected result and original acquisition token. Implementations own contexts.</summary>
internal interface IOutboxStateStore
{
    Task<OutboxOperationResult> ClaimAsync(Guid operation, int size, TimeSpan duration, CancellationToken ct);
    Task<OutboxOperationResult> ApplyAsync(Guid operation, IReadOnlyList<OutboxChange> changes, CancellationToken ct);
    Task<OutboxOperationResult> RecoverAsync(Guid operation, int size, CancellationToken ct);
    Task<OutboxOperationResult> ClaimHandoffsAsync(Guid operation, int size, TimeSpan duration, CancellationToken ct);
    Task<bool> OwnsHandoffAsync(HandoffClaim claim, CancellationToken ct);
    Task<OutboxOperationResult> AcknowledgeHandoffAsync(Guid operation, HandoffClaim claim, bool delivered, CancellationToken ct);
    Task<OutboxOperationResult> ResolveAsync(Guid operation, Guid id, Guid? tenant, Guid cycle, string reason,
        bool replay, IAuditOutboxRecoveryPermit permit, CancellationToken ct);
    Task<OutboxOperationResult> CleanupAsync(Guid operation, Guid id, Guid? tenant, Guid cycle,
        IAuditOutboxRecoveryPermit permit, CancellationToken ct);
    Task<OutboxOperationResult> CleanupCompletedAsync(Guid operation, Guid id, Guid tenant,
        DateTimeOffset completedBefore, IAuditOutboxRecoveryPermit permit, CancellationToken ct);
}
