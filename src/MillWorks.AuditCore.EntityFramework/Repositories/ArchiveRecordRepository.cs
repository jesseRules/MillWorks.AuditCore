using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// Archive Record Repository implementation for managing audit archive metadata
/// </summary>
/// <param name="context">Database context</param>
public sealed class ArchiveRecordRepository(AuditDbContext context)
    : Repository<AuditArchiveRecordEntity>(context), IArchiveRecordRepository
{
    /// <summary>
    /// Gets an archive record by its archive ID
    /// </summary>
    /// <param name="archiveId">Unique archive identifier</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Archive record entity or null if not found</returns>
    public async Task<AuditArchiveRecordEntity?> GetByArchiveIdAsync(string archiveId,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .FirstOrDefaultAsync(ar => ar.ArchiveId == archiveId, cancellationToken);
    }

    /// <summary>
    /// Gets all archive records ordered by creation date (newest first)
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetAllOrderedAsync(
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .OrderByDescending(static ar => ar.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets archive records by status
    /// </summary>
    /// <param name="status">Archive status to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities with the specified status</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetByStatusAsync(MillWorksArchiveStatus status,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ar => ar.Status == status)
            .OrderByDescending(static ar => ar.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets archive records within a date range
    /// </summary>
    /// <param name="startDate">Start date for archive creation</param>
    /// <param name="endDate">End date for archive creation</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities created within the date range</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetByDateRangeAsync(DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ar => ar.CreatedAt >= startDate && ar.CreatedAt <= endDate)
            .OrderByDescending(static ar => ar.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets archive records that contain events within a specific date range
    /// </summary>
    /// <param name="eventStartDate">Start date of events to find archives for</param>
    /// <param name="eventEndDate">End date of events to find archives for</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities containing events in the specified range</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetByEventDateRangeAsync(DateTimeOffset eventStartDate,
        DateTimeOffset eventEndDate, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ar =>
                ar.Status == MillWorksArchiveStatus.Completed &&
                ((ar.DateRangeStart <= eventStartDate && ar.DateRangeEnd >= eventStartDate) ||
                 (ar.DateRangeStart <= eventEndDate && ar.DateRangeEnd >= eventEndDate) ||
                 (ar.DateRangeStart >= eventStartDate && ar.DateRangeEnd <= eventEndDate)))
            .OrderBy(static ar => ar.DateRangeStart)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets archives that need integrity verification (haven't been verified recently)
    /// </summary>
    /// <param name="verificationIntervalHours">Hours since last verification to consider as needing verification</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities that need verification</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetArchivesNeedingVerificationAsync(
        int verificationIntervalHours = 24, CancellationToken cancellationToken = default)
    {
        var cutoffTime = DateTimeOffset.UtcNow.AddHours(-verificationIntervalHours);

        return await DbSet.AsNoTracking()
            .Where(ar =>
                ar.Status == MillWorksArchiveStatus.Completed &&
                (ar.LastVerifiedAt == null || ar.LastVerifiedAt < cutoffTime))
            .OrderBy(static ar => ar.LastVerifiedAt ?? DateTimeOffset.MinValue)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Updates the verification timestamp for an archive
    /// </summary>
    /// <param name="archiveId">Archive ID to update</param>
    /// <param name="verifiedAt">Timestamp when verification occurred</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the record was updated, false if not found</returns>
    public async Task<bool> UpdateVerificationTimestampAsync(string archiveId, DateTimeOffset verifiedAt,
        CancellationToken cancellationToken = default)
    {
        var rowsAffected = await DbSet
            .Where(ar => ar.ArchiveId == archiveId)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(static ar => ar.LastVerifiedAt, verifiedAt),
                cancellationToken);

        return rowsAffected > 0;
    }

    /// <summary>
    /// Updates the status of an archive record
    /// </summary>
    /// <param name="archiveId">Archive ID to update</param>
    /// <param name="status">New status</param>
    /// <param name="errorMessage">Optional error message if status is Failed</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if the record was updated, false if not found</returns>
    public async Task<bool> UpdateStatusAsync(string archiveId, MillWorksArchiveStatus status,
        string? errorMessage = null,
        CancellationToken cancellationToken = default)
    {
        var rowsAffected = await DbSet
            .Where(ar => ar.ArchiveId == archiveId)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(static ar => ar.Status, status)
                    .SetProperty(static ar => ar.ErrorMessage, errorMessage),
                cancellationToken);

        return rowsAffected > 0;
    }

    /// <summary>
    /// Gets archives by blob container name
    /// </summary>
    /// <param name="containerName">Container name to filter by</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Collection of archive record entities in the specified container</returns>
    public async Task<IEnumerable<AuditArchiveRecordEntity>> GetByContainerNameAsync(string containerName,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ar => ar.ContainerName == containerName)
            .OrderByDescending(static ar => ar.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the total size of all archives
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Total size in bytes of all completed archives</returns>
    public async Task<long> GetTotalArchiveSizeAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(static ar => ar.Status == MillWorksArchiveStatus.Completed)
            .SumAsync(static ar => ar.SizeBytes, cancellationToken);
    }

    /// <summary>
    /// Gets archive statistics
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Dictionary containing archive statistics</returns>
    public async Task<Dictionary<string, object>> GetArchiveStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        var stats = new Dictionary<string, object>();

        // Total archives by status
        var statusCounts = await DbSet.AsNoTracking()
            .GroupBy(static ar => ar.Status)
            .Select(static g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(static x => x.Status.ToString(), static x => (object)x.Count, cancellationToken);

        foreach (var statusCount in statusCounts)
        {
            stats[$"Archives{statusCount.Key}"] = statusCount.Value;
        }

        // Total events archived
        stats["TotalEventsArchived"] = await DbSet.AsNoTracking()
            .Where(static ar => ar.Status == MillWorksArchiveStatus.Completed)
            .SumAsync(static ar => ar.EventCount, cancellationToken);

        // Total storage used
        stats["TotalStorageBytes"] = await GetTotalArchiveSizeAsync(cancellationToken);

        // Average archive size (computed server-side)
        var completedQuery = DbSet.AsNoTracking()
            .Where(static ar => ar.Status == MillWorksArchiveStatus.Completed);

        var completedCount = await completedQuery.CountAsync(cancellationToken);
        if (completedCount > 0)
        {
            stats["AverageArchiveSizeBytes"] = (long)await completedQuery.AverageAsync(static ar => ar.SizeBytes, cancellationToken);
            stats["AverageEventsPerArchive"] = (int)await completedQuery.AverageAsync(static ar => ar.EventCount, cancellationToken);
        }

        // Oldest and newest archives
        var oldestArchive = await completedQuery
            .OrderBy(static ar => ar.DateRangeStart)
            .Select(static ar => (DateTimeOffset?)ar.DateRangeStart)
            .FirstOrDefaultAsync(cancellationToken);

        var newestArchive = await completedQuery
            .OrderByDescending(static ar => ar.DateRangeEnd)
            .Select(static ar => (DateTimeOffset?)ar.DateRangeEnd)
            .FirstOrDefaultAsync(cancellationToken);

        stats["OldestArchivedEvent"] = oldestArchive ?? new DateTimeOffset();
        stats["NewestArchivedEvent"] = newestArchive ?? new DateTimeOffset();

        // Archives needing verification (count server-side)
        var cutoffTimeForVerification = DateTimeOffset.UtcNow.AddHours(-24);
        stats["ArchivesNeedingVerification"] = await DbSet.AsNoTracking()
            .CountAsync(ar =>
                ar.Status == MillWorksArchiveStatus.Completed &&
                (ar.LastVerifiedAt == null || ar.LastVerifiedAt < cutoffTimeForVerification), cancellationToken);

        return stats;
    }

    /// <summary>
    /// Deletes archive records older than the specified retention period
    /// </summary>
    /// <param name="retentionDays">Number of days to retain archive records</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of archive records deleted</returns>
    public async Task<int> CleanupOldArchiveRecordsAsync(int retentionDays,
        CancellationToken cancellationToken = default)
    {
        var cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        // Use ExecuteDeleteAsync for proper schema qualification on SQL Server
        return await Context.Set<AuditArchiveRecordEntity>()
            .Where(ar => ar.CreatedAt < cutoffDate && ar.Status != MillWorksArchiveStatus.InProgress)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>
    /// Gets paginated archive records
    /// </summary>
    /// <param name="pageNumber">Page number (1-based)</param>
    /// <param name="pageSize">Number of records per page</param>
    /// <param name="status">Optional status filter</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Tuple containing the page of archive records and total count</returns>
    public async Task<(IEnumerable<AuditArchiveRecordEntity> Records, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        MillWorksArchiveStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking();

        if (status.HasValue)
        {
            query = query.Where(ar => ar.Status == status.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var records = await query
            .OrderByDescending(static ar => ar.CreatedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (records, totalCount);
    }
}