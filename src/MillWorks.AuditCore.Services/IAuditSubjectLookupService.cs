using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>Tenant-scoped, payload-independent discovery of AuditCore records linked to a subject.</summary>
public interface IAuditSubjectLookupService
{
    /// <summary>
    /// Returns a stable keyset page across explicit events, entity-change logs, and security events.
    /// The method returns record locations only; disclosure/redaction remains the host's responsibility.
    /// </summary>
    Task<AuditSubjectLookupPage> FindAsync(
        Guid tenantId,
        Guid subjectId,
        AuditSubjectLookupCursor? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default);
}
