using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Extension point for deciding whether a failed audit-log build attempt should
/// propagate out of the EF audit interceptor (fail-closed) or be swallowed
/// (permissive). Invoked from <c>AuditSaveChangesInterceptor</c>'s catch path.
/// The default implementation inspects entity attributes (<c>[FERPA]</c>,
/// <c>[PHI]</c>, <c>[SensitiveData(ApplicableStandards = ...)]</c>) against the
/// regulated standards HIPAA, FERPA, GDPR, PCI_DSS. Consumers can register a
/// custom implementation for tenant-, operation-, or user-specific rules.
/// </summary>
public interface IAuditFailurePolicy
{
    /// <summary>
    /// Decides whether the audit failure described by <paramref name="context"/>
    /// should cause the business transaction to roll back.
    /// </summary>
    /// <param name="context">Failure context — configured mode plus the entities captured from the failing save.</param>
    /// <returns><c>true</c> to propagate <c>AuditIntegrityException</c>; <c>false</c> to swallow and log.</returns>
    bool ShouldFailClosed(AuditFailureContext context);
}
