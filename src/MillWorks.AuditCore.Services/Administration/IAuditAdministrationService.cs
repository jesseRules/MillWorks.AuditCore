using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Administration;

/// <summary>Operations requiring explicit host authority in addition to audit read access.</summary>
public enum AuditAdministrationOperation { Export, Anonymize, Archive, Restore, ExportProof }

/// <summary>The exact target authorized for one administration call.</summary>
public sealed record AuditAdministrationTarget(AuditAdministrationOperation Operation,
    Guid? SubjectUserId = null, string? ArchiveId = null, DateTimeOffset? ArchiveBefore = null, Guid? EventId = null);

/// <summary>Trusted caller context supplied by the host, never by request data.</summary>
public sealed record AuditAdministrationScope(Guid TenantId, Guid ActorId, string CorrelationId);

/// <summary>Host-owned authority and any legal-hold locks, retained through completion.</summary>
public interface IAuditAdministrationPermit : IAsyncDisposable
{
    /// <summary>The authorized caller's tenant and identity.</summary>
    AuditAdministrationScope Scope { get; }
    /// <summary>Rechecks caller authority before executing the authorized target.</summary>
    Task CheckCurrentAsync(CancellationToken cancellationToken);
}

/// <summary>Fail-closed host adapter for operation, subject, tenant, and legal-hold checks.</summary>
public interface IAuditAdministrationAuthority
{
    /// <summary>Authorizes the exact target; throws when caller authority or scope is unavailable.</summary>
    Task<IAuditAdministrationPermit> AcquireAsync(AuditAdministrationTarget target, CancellationToken cancellationToken);
}

/// <summary>Administration cannot proceed without changing retained evidence or violating policy.</summary>
public sealed class AuditAdministrationConflictException(string message) : InvalidOperationException(message);

/// <summary>Tenant-bound administrative operations. No client-supplied tenant override is accepted.</summary>
public interface IAuditAdministrationService
{
    /// <summary>Exports integrity proof only for an event owned by the authorized tenant.</summary>
    Task<byte[]> ExportProofAsync(Guid eventId, CancellationToken cancellationToken = default);
    /// <summary>Exports only explicitly disclosable metadata for a tenant-authorized subject.</summary>
    Task<AuditExportResult> ExportAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Anonymizes unprotected events only; immutable evidence must use governed erasure.</summary>
    Task<AnonymizationResult> AnonymizeAsync(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Archives only events belonging to the authorized caller's tenant.</summary>
    Task<AuditArchivalResult> ArchiveAsync(DateTimeOffset archiveBefore, CancellationToken cancellationToken = default);
    /// <summary>Restores only a tenant-owned archive into the same tenant.</summary>
    Task<AuditRestoreResult> RestoreAsync(string archiveId, CancellationToken cancellationToken = default);
}
