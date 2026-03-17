using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.TamperDetection.Interfaces;

/// <summary>
/// Interface for tamper detection service
/// </summary>
public interface ITamperDetectionService
{
    /// <summary>
    /// Creates an integrity record for a given audit event.
    /// </summary>
    /// <param name="auditEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditIntegrityDto> CreateIntegrityRecordAsync(AuditIntegrityDto auditEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates integrity records for a batch of audit events atomically.
    /// All events are chained sequentially within a single lock acquisition.
    /// </summary>
    Task<IReadOnlyList<AuditIntegrityDto>> CreateIntegrityRecordBatchAsync(
        IReadOnlyList<AuditIntegrityDto> auditEvents, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of a specific audit event by its ID.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> VerifyIntegrityAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of the audit chain within a specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<TamperDetectionResult> VerifyChainIntegrityAsync(DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies the integrity of the entire sequence of audit events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> VerifySequenceIntegrityAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detects tampering events within a specified time frame.
    /// </summary>
    /// <param name="hoursBack"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<TamperAlert>> DetectTamperingAsync(int hoursBack = 24, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports the integrity proof for a specific audit event as a byte array.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<byte[]> ExportIntegrityProofAsync(Guid eventId, CancellationToken cancellationToken = default);

}