using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Dto;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for audit archival service
/// </summary>
public interface IAuditArchivalService
{
    /// <summary>
    /// Archives audit events older than the specified date.
    /// </summary>
    /// <param name="archiveBefore"></param>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditArchivalResult> ArchiveAuditEventsAsync(
        DateTimeOffset archiveBefore,
        string? archiveId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores archived audit events by their archive ID.
    /// </summary>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditRestoreResult>
        RestoreArchivedEventsAsync(string archiveId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of all archived audit events with their metadata.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<ArchiveMetadata>> GetArchivesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the integrity of an archived audit event by its archive ID.
    /// </summary>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> ValidateArchiveIntegrityAsync(string archiveId, CancellationToken cancellationToken = default);
}