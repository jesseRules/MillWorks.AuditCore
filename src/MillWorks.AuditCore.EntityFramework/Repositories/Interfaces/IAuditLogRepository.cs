using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

/// <summary>
/// Interface for Audit Log Repository
/// </summary>
public interface IAuditLogRepository : IRepository<AuditLogEntity>
{
    /// <summary>
    /// Gets the audit trail for a specific entity by its name and ID.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogEntity>> GetEntityAuditTrailAsync(string entityName, Guid entityId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the user activity logs for a specific user.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take">Maximum number of records to return</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogEntity>> GetUserActivityAsync(Guid userId, DateTimeOffset? fromDate = null,
        int take = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets action counts grouped by action type (server-side aggregation).
    /// </summary>
    Task<List<KeyValuePair<string, int>>> GetActionCountsAsync(Guid? userId = null,
        DateTimeOffset? fromDate = null, CancellationToken cancellationToken = default);
}