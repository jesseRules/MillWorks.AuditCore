namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown when a compliance enforcement check fails and <see cref="Dto.ComplianceEnforcementMode.Enforce"/> is active.
/// This exception must NOT be caught by the interceptor's generic exception handler — it must propagate
/// to the caller so the non-compliant SaveChanges is rejected.
/// </summary>
public sealed class ComplianceViolationException : Exception
{
    /// <summary>
    /// The compliance standard that was violated (e.g., "FERPA").
    /// </summary>
    public string Standard { get; }

    /// <summary>
    /// The entity type that triggered the violation.
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// The user ID associated with the operation, if available.
    /// </summary>
    public string? UserId { get; }

    /// <summary>
    /// The regulation reference for the violation (e.g., "34 CFR §99.30").
    /// </summary>
    public string? RegulationReference { get; }

    public ComplianceViolationException(
        string standard,
        string entityType,
        string? userId,
        string? regulationReference,
        string message)
        : base(message)
    {
        Standard = standard;
        EntityType = entityType;
        UserId = userId;
        RegulationReference = regulationReference;
    }

    public ComplianceViolationException(
        string standard,
        string entityType,
        string? userId,
        string? regulationReference,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        Standard = standard;
        EntityType = entityType;
        UserId = userId;
        RegulationReference = regulationReference;
    }
}
