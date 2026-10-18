using MillWorks.AuditCore.Abstractions.Attributes;
using System.ComponentModel.DataAnnotations;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>Immutable SQL commit evidence. Retain until all referenced work is resolved and
/// the reconciliation window has elapsed; these are not backup-independent disposition receipts.</summary>
[NoAudit] // Audit-owned evidence/projection; do not recursively audit its bookkeeping.
public sealed class AuditOutboxOperation : IAppendOnlyEntity
{
    [Key] public Guid Id { get; set; }
    [MaxLength(64)] public required string RequestHash { get; set; }
    [MaxLength(40)] public required string Kind { get; set; }
    public required string ResultJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset VerifyUntil { get; set; }
}

/// <summary>External projection of a retained source failure. Delivery never replays the audit event.</summary>
[NoAudit] // Audit-owned evidence/projection; do not recursively audit its bookkeeping.
public sealed class AuditOutboxFailureHandoff
{
    [Key] public Guid Id { get; set; }
    public Guid OutboxRowId { get; set; }
    public Guid FailureCycleId { get; set; }
    public Guid? TenantId { get; set; }
    public required string EnvelopeJson { get; set; }
    [MaxLength(80)] public required string ReasonCode { get; set; }
    public AuditOutboxHandoffStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? NextRetryAt { get; set; }
    [MaxLength(100)] public string? ClaimToken { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int AttemptCount { get; set; }
}

public enum AuditOutboxHandoffStatus
{
    Pending,
    Dispatching,
    Delivered,
    Suppressed
}

/// <summary>Retained operator evidence; a successful projection alone is never source resolution.</summary>
[NoAudit] // Audit-owned evidence/projection; do not recursively audit its bookkeeping.
public sealed class AuditOutboxResolution : IAppendOnlyEntity
{
    [Key] public Guid Id { get; set; }
    public Guid OutboxRowId { get; set; }
    public Guid FailureCycleId { get; set; }
    public Guid? TenantId { get; set; }
    [MaxLength(256)] public required string ActorId { get; set; }
    [MaxLength(2000)] public required string Reason { get; set; }
    [MaxLength(16)] public required string Action { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
