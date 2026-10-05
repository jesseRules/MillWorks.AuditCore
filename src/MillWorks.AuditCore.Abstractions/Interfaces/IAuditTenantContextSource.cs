namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Supplies the tenant governing the current consumer-context operation when an audited entity
/// does not carry its own mapped <c>TenantId</c> property.
/// </summary>
/// <remarks>
/// Entity-level tenant metadata takes precedence. A missing value does not create a global or
/// synthetic tenant; subject-indexed writes that cannot resolve either source fail closed.
/// </remarks>
public interface IAuditTenantContextSource
{
    /// <summary>Gets the non-empty tenant currently governing the consumer context.</summary>
    Guid? CurrentAuditTenantId { get; }
}
