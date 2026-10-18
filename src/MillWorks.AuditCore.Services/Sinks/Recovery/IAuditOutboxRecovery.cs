namespace MillWorks.AuditCore.Services.Sinks.Recovery;

public enum AuditOutboxRecoveryAction { Inspect, Replay, Skip, Cleanup }
public sealed record AuditOutboxRecoveryTarget(Guid RowId, Guid? TenantId, Guid? FailureCycleId);

/// <summary>Host-owned authorization and current hold/disposition authority. No permissive default
/// is registered. A null tenant is a separate quarantine scope, never a cross-tenant wildcard.</summary>
public interface IAuditOutboxRecoveryAuthorization
{
    ValueTask<IAuditOutboxRecoveryPermit> AcquireAsync(AuditOutboxRecoveryTarget target,
        AuditOutboxRecoveryAction action, CancellationToken cancellationToken);
}

/// <summary>Keep authorization/hold coordination valid until the SQL transaction commits.
/// The host derives ActorId from its trusted caller, never from client-supplied audit payloads.</summary>
public interface IAuditOutboxRecoveryPermit : IAsyncDisposable
{
    string ActorId { get; }
    Guid? TenantId { get; }
    ValueTask CheckCurrentAsync(CancellationToken cancellationToken);
}

public sealed record AuditOutboxFailureSummary(Guid RowId, Guid? TenantId, Guid FailureCycleId,
    int AttemptCount, string ReasonCode, bool Resolved, int PendingProjections, int DeliveredProjections);
public sealed record AuditOutboxResolutionResult(Guid ResolutionId, bool Applied, bool ExternalDeliveryMayBeInFlight);

/// <summary>Supported operator boundary. Inspection never returns payloads. Replay/skip require
/// the observed failure-cycle identity and retain actor/time/reason evidence. Cleanup is opt-in.</summary>
public interface IAuditOutboxRecovery
{
    Task<AuditOutboxFailureSummary?> InspectAsync(Guid rowId, Guid? tenantId, CancellationToken cancellationToken = default);
    Task<AuditOutboxResolutionResult> ReplayAsync(Guid resolutionId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        string reason, CancellationToken cancellationToken = default);
    Task<AuditOutboxResolutionResult> SkipAsync(Guid resolutionId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        string reason, CancellationToken cancellationToken = default);
    /// <summary>Remove explicitly skipped or successfully replayed source/projection payloads only after the 24-hour commit-verification
    /// window and host approval. Immutable metadata receipts/resolution evidence are retained.</summary>
    Task<bool> CleanupAsync(Guid operationId, Guid rowId, Guid? tenantId, Guid failureCycleId,
        CancellationToken cancellationToken = default);
    /// <summary>Remove an ordinary completed source only with host tenant/hold authority,
    /// retained identity evidence and expired commit-verification windows. Failed/replayed
    /// sources require the failure-cycle cleanup operation instead.</summary>
    Task<bool> CleanupCompletedAsync(Guid operationId, Guid rowId, Guid tenantId,
        DateTimeOffset completedBefore, CancellationToken cancellationToken = default);

}
