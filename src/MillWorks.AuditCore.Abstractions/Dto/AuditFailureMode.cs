namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Controls how the audit pipeline responds to write failures inside the EF
/// SaveChangesAsync interceptor. When fail-closed applies, the interceptor
/// rethrows <see cref="Exceptions.AuditIntegrityException"/> and the business
/// transaction rolls back.
/// </summary>
public enum AuditFailureMode
{
    /// <summary>
    /// Default. Audit write failures are logged and swallowed; the business
    /// write proceeds. Matches the historical "audit must never break the
    /// application's SaveChanges" behavior.
    /// </summary>
    Permissive = 0,

    /// <summary>
    /// Rethrow only when any modified entity in the failing save is decorated
    /// with <c>[FERPA]</c>, <c>[PHI]</c>, or has a <c>[SensitiveData]</c>
    /// property whose <c>ApplicableStandards</c> include a regulated regime
    /// (HIPAA, FERPA, GDPR, PCI_DSS). Non-regulated entities remain permissive.
    /// </summary>
    FailClosedForRegulated = 1,

    /// <summary>
    /// Rethrow on every audit failure, regardless of entity regulation. Use
    /// when audit completeness is non-negotiable across all data.
    /// </summary>
    FailClosedAlways = 2
}

/// <summary>
/// Selects which <c>IAuditSink</c> implementation handles audit envelopes at
/// runtime. Default is <see cref="Immediate"/>; <see cref="TransactionalOutbox"/>
/// is the opt-in posture for deployments that require audit + business writes
/// to succeed atomically (regulated / zero-loss durability).
/// </summary>
public enum AuditSinkMode
{
    /// <summary>
    /// Audit writes happen on the audit-owned DbContext / connection.
    /// Decoupled from the consumer's transaction. Default.
    /// </summary>
    Immediate = 0,

    /// <summary>
    /// Audit writes participate in the consumer's transaction via outbox
    /// row. A background drainer commits the outbox row's payload to the
    /// audit DbContext after commit. Required for FailClosedForRegulated
    /// when business + audit must succeed atomically.
    /// </summary>
    TransactionalOutbox = 1,
}
