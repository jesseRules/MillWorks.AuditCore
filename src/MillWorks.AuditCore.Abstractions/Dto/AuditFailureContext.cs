namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Context passed to <see cref="Interfaces.IAuditFailurePolicy"/> when the EF audit
/// interceptor fails to build audit log records. The policy uses this context to
/// decide whether the failure should propagate out of <c>SavingChangesAsync</c>
/// (fail-closed) or be swallowed (permissive).
/// </summary>
/// <param name="FailureMode">The configured <see cref="AuditFailureMode"/>.</param>
/// <param name="Entities">
/// The entities that were part of the failing save, materialized before audit-record
/// construction so the policy can inspect regulated attributes even when the failure
/// happened partway through.
/// </param>
public sealed record AuditFailureContext(
    AuditFailureMode FailureMode,
    IReadOnlyList<AuditFailureEntity> Entities);

/// <summary>
/// A single entity captured from a failing audit attempt: its CLR type and the
/// EF change-tracker state ("Added", "Modified", "Deleted") at the point of failure.
/// </summary>
/// <param name="EntityType">CLR type of the entity.</param>
/// <param name="Action">EF state name — one of "Added", "Modified", "Deleted".</param>
public sealed record AuditFailureEntity(
    Type EntityType,
    string Action);
