namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for audit maintenance operations
/// </summary>
public interface IAuditMaintenanceService
{
    /// <summary>
    /// Cleans up old audit events based on the specified retention period.
    /// </summary>
    /// <param name="retentionDays"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<int> CleanupOldAuditEventsAsync(int retentionDays, CancellationToken cancellationToken = default);

    /// <summary>
    /// Archives audit events that are older than the specified date to a specified location.
    /// </summary>
    /// <param name="archiveBefore"></param>
    /// <param name="archiveLocation"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<int> ArchiveAuditEventsAsync(DateTimeOffset archiveBefore, string archiveLocation,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the size of the audit database in bytes.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<long> GetAuditDatabaseSizeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Optimizes audit tables by performing maintenance tasks such as reindexing and updating statistics.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> OptimizeAuditTablesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets statistics about the audit logs, such as total events, distinct users, and event types.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Dictionary<string, object?>> GetAuditStatisticsAsync(CancellationToken cancellationToken = default);
}