using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Maintenance;

/// <summary>
/// A service for maintaining and managing audit logs.
/// </summary>
/// <param name="context"></param>
/// <param name="logger"></param>
public sealed class AuditMaintenanceService(
    AuditDbContext context,
    ILogger<AuditMaintenanceService> logger)
    : IAuditMaintenanceService
{
    /// <summary>
    /// Cleans up old audit events based on the specified retention period.
    /// <param name="retentionDays"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// </summary>
    public async Task<int> CleanupOldAuditEventsAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting cleanup of audit events older than {RetentionDays} days", retentionDays);

        DateTimeOffset cutoffDate = DateTimeOffset.UtcNow.AddDays(-retentionDays);

        try
        {
            // Delete in batches server-side to avoid locking issues
            int totalDeleted = 0;
            int batchSize = 1000;
            int deleted;

            do
            {
                deleted = await context.AuditEvents
                    .Where(e => e.InsertedDate < cutoffDate)
                    .Take(batchSize)
                    .ExecuteDeleteAsync(cancellationToken);

                totalDeleted += deleted;

                if (deleted > 0)
                {
                    logger.LogDebug("Deleted {Count} audit events in batch", deleted);
                }
            } while (deleted > 0 && !cancellationToken.IsCancellationRequested);

            logger.LogInformation("Cleanup completed. Total audit events deleted: {TotalDeleted}", totalDeleted);

            return totalDeleted;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during audit cleanup");
            throw;
        }
    }

    /// <summary>
    /// Archives audit events that are older than the specified date to a specified location.
    /// </summary>
    /// <param name="archiveBefore"></param>
    /// <param name="archiveLocation"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<int> ArchiveAuditEventsAsync(
        DateTimeOffset archiveBefore,
        string archiveLocation,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Starting archive of audit events before {ArchiveBefore} to {Location}",
            archiveBefore, archiveLocation);

        int eventsToArchive = await context.AuditEvents
            .Where(e => e.InsertedDate < archiveBefore)
            .CountAsync(cancellationToken);

        logger.LogWarning("Archive functionality not yet implemented. Would archive {Count} events", eventsToArchive);

        return eventsToArchive;
    }

    /// <summary>
    /// Gets the size of the audit database in bytes.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<long> GetAuditDatabaseSizeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Use FromSqlInterpolated for safe parameterized queries
            var schemaName = "audit";
            var tableName = "AuditEvents";

            var result = await context.Database
                .SqlQuery<DatabaseSizeResult>($@"
                SELECT
                    SUM(a.total_pages) * 8 AS TotalSpaceKB
                FROM
                    sys.tables t
                INNER JOIN sys.indexes i ON t.OBJECT_ID = i.object_id
                INNER JOIN sys.partitions p ON i.object_id = p.OBJECT_ID AND i.index_id = p.index_id
                INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
                WHERE
                    t.Name = {tableName} AND t.schema_id = SCHEMA_ID({schemaName})
                GROUP BY
                    t.Name")
                .FirstOrDefaultAsync(cancellationToken);

            return result?.TotalSpaceKb ?? 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting audit database size");

            // Fallback: estimate based on record count
            long count = await context.AuditEvents.LongCountAsync(cancellationToken);
            const long estimatedSizePerRecord = 2048; // 2KB average per record
            return count * estimatedSizePerRecord;
        }
    }


    /// <summary>
    /// Database size result
    /// </summary>
    private sealed class DatabaseSizeResult
    {
        /// <summary>
        /// Total space in KB
        /// </summary>
        public long TotalSpaceKb { get; set; }
    }

    /// <summary>
    /// Optimizes audit tables for performance.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<bool> OptimizeAuditTablesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            logger.LogInformation("Optimizing audit tables");

            await context.Database.ExecuteSqlRawAsync(
                "UPDATE STATISTICS [audit].[AuditEvents]", cancellationToken);

            logger.LogInformation("Audit table optimization completed");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error optimizing audit tables");
            return false;
        }
    }

    /// <summary>
    /// Gets statistics about the audit logs, such as total events, distinct users, and event types.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, object?>> GetAuditStatisticsAsync(
        CancellationToken cancellationToken = default)
    {
        var stats = new Dictionary<string, object?>();

        try
        {
            // Total events
            stats["TotalEvents"] = await context.AuditEvents.CountAsync(cancellationToken);

            // Events by date
            DateTimeOffset now = DateTimeOffset.UtcNow;
            DateTimeOffset today = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);
            stats["EventsToday"] = await context.AuditEvents
                .CountAsync(e => e.InsertedDate >= today, cancellationToken);

            DateTimeOffset thisWeek = today.AddDays(-(int)today.DayOfWeek);
            stats["EventsThisWeek"] = await context.AuditEvents
                .CountAsync(e => e.InsertedDate >= thisWeek, cancellationToken);

            DateTimeOffset thisMonth = new DateTimeOffset(today.Year, today.Month, 1, 0, 0, 0, TimeSpan.Zero);
            stats["EventsThisMonth"] = await context.AuditEvents
                .CountAsync(e => e.InsertedDate >= thisMonth, cancellationToken);

            // Oldest and newest events
            var oldestEvent = await context.AuditEvents
                .OrderBy(static e => e.InsertedDate)
                .Select(static e => e.InsertedDate)
                .FirstOrDefaultAsync(cancellationToken);
            stats["OldestEvent"] = oldestEvent;

            var newestEvent = await context.AuditEvents
                .OrderByDescending(static e => e.InsertedDate)
                .Select(static e => e.InsertedDate)
                .FirstOrDefaultAsync(cancellationToken);
            stats["NewestEvent"] = newestEvent;

            // Database size
            stats["DatabaseSizeKB"] = await GetAuditDatabaseSizeAsync(cancellationToken) / 1024;

            // Event types
            var eventTypeCounts = await context.AuditEvents
                .Where(static e => e.EventType != null)
                .GroupBy(static e => e.EventType)
                .Select(static g => new { Type = g.Key, Count = g.Count() })
                .OrderByDescending(static x => x.Count)
                .Take(10)
                .ToDictionaryAsync(static x => x.Type!, static x => (object)x.Count, cancellationToken);
            stats["TopEventTypes"] = eventTypeCounts;

            // Users
            stats["UniqueUsers"] = await context.AuditEvents
                .Where(static e => e.User != null)
                .Select(static e => e.User)
                .Distinct()
                .CountAsync(cancellationToken);

            return stats;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting audit statistics");
            stats["Error"] = ex.Message;
            return stats;
        }
    }
}
