using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

/// <summary>
/// Interface for Archive Record Repository
/// </summary>
public interface IArchiveRecordRepository : IRepository<AuditArchiveRecordEntity>
{
    /// <summary>
    /// Gets an archive record by its archive ID
    /// </summary>
    /// <param name="archiveId">Unique archive identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Archive record entity or null if not found</returns>
    Task<AuditArchiveRecordEntity?>
        GetByArchiveIdAsync(string archiveId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all archive records ordered by creation date (newest first)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetAllOrderedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archive records by status
    /// </summary>
    /// <param name="status">Archive status to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities with the specified status</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetByStatusAsync(MillWorksArchiveStatus status,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archive records within a date range
    /// </summary>
    /// <param name="startDate">Start date for archive creation</param>
    /// <param name="endDate">End date for archive creation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities created within the date range</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetByDateRangeAsync(DateTimeOffset startDate, DateTimeOffset endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archive records that contain events within a specific date range
    /// </summary>
    /// <param name="eventStartDate">Start date of events to find archives for</param>
    /// <param name="eventEndDate">End date of events to find archives for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities containing events in the specified range</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetByEventDateRangeAsync(DateTimeOffset eventStartDate,
        DateTimeOffset eventEndDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archives that need integrity verification (haven't been verified recently)
    /// </summary>
    /// <param name="verificationIntervalHours">Hours since last verification to consider as needing verification</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities that need verification</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetArchivesNeedingVerificationAsync(int verificationIntervalHours = 24,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the verification timestamp for an archive
    /// </summary>
    /// <param name="archiveId">Archive ID to update</param>
    /// <param name="verifiedAt">Timestamp when verification occurred</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the record was updated, false if not found</returns>
    Task<bool> UpdateVerificationTimestampAsync(string archiveId, DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the status of an archive record
    /// </summary>
    /// <param name="archiveId">Archive ID to update</param>
    /// <param name="status">New status</param>
    /// <param name="errorMessage">Optional error message if status is Failed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the record was updated, false if not found</returns>
    Task<bool> UpdateStatusAsync(string archiveId, MillWorksArchiveStatus status, string? errorMessage = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archives by blob container name
    /// </summary>
    /// <param name="containerName">Container name to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities in the specified container</returns>
    Task<IEnumerable<AuditArchiveRecordEntity>> GetByContainerNameAsync(string containerName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the total size of all archives
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Total size in bytes of all completed archives</returns>
    Task<long> GetTotalArchiveSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets archive statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary containing archive statistics</returns>
    Task<Dictionary<string, object>> GetArchiveStatisticsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes archive records older than the specified retention period
    /// </summary>
    /// <param name="retentionDays">Number of days to retain archive records</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of archive records deleted</returns>
    Task<int> CleanupOldArchiveRecordsAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets paginated archive records
    /// </summary>
    /// <param name="pageNumber">Page number (1-based)</param>
    /// <param name="pageSize">Number of records per page</param>
    /// <param name="status">Optional status filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the page of archive records and total count</returns>
    Task<(IEnumerable<AuditArchiveRecordEntity> Records, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        MillWorksArchiveStatus? status = null,
        CancellationToken cancellationToken = default);
}