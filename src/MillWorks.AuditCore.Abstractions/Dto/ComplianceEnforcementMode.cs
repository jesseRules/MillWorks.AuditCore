namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Controls how the compliance subsystem responds to violations detected at save time.
/// </summary>
public enum ComplianceEnforcementMode
{
    /// <summary>
    /// Log warnings only. Non-compliant operations proceed. This is the default and matches
    /// pre-enforcement behavior.
    /// </summary>
    Advisory,

    /// <summary>
    /// Log warnings and create security audit events for non-compliant operations.
    /// Operations still proceed.
    /// </summary>
    AuditOnly,

    /// <summary>
    /// Block non-compliant operations by throwing <see cref="MillWorks.AuditCore.Abstractions.Exceptions.ComplianceViolationException"/>.
    /// Fail-closed: if consent state is indeterminate (cache miss, service error), the operation is blocked.
    /// </summary>
    Enforce
}
