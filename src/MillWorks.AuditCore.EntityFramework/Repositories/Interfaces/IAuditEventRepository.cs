using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

/// <summary>
/// Interface for Audit Event Repository
/// </summary>
public interface IAuditEventRepository : IRepository<AuditEventEntity>
{
    /// <summary>
    /// Gets audit events by event type.
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByEventTypeAsync(string eventType,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events for a specific user.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByUserIdAsync(Guid userId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events for a specific AspNet user.
    /// </summary>
    /// <param name="aspNetUserId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByAspNetUserIdAsync(string aspNetUserId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events for a specific entity.
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="entityId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByEntityAsync(string entityType, string entityId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events within a date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByDateRangeAsync(DateTimeOffset startDate, DateTimeOffset endDate,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events by correlation ID.
    /// </summary>
    /// <param name="correlationId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByCorrelationIdAsync(string correlationId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events by tenant ID.
    /// </summary>
    /// <param name="tenantId"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByTenantIdAsync(Guid tenantId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events by action performed.
    /// </summary>
    /// <param name="action"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByActionAsync(string action,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events with duration longer than specified milliseconds.
    /// </summary>
    /// <param name="minimumDuration"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByMinimumDurationAsync(int minimumDuration,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events by environment.
    /// </summary>
    /// <param name="environment"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetByEnvironmentAsync(string environment,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events including their integrity records.
    /// </summary>
    /// <param name="eventIds"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetWithIntegrityAsync(IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit events within a date range for tamper detection.
    /// </summary>
    /// <param name="fromDate"></param>
    /// <param name="maxResults">Maximum number of results to return (safety limit)</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditEventEntity>> GetForTamperDetectionAsync(DateTimeOffset fromDate,
        int maxResults = 10000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Events whether an audit event exists by its ID.
    /// </summary>
    /// <param name="auditEventEventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> EventExistsAsync(Guid auditEventEventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the subset of the given EventIds that already exist in the database.
    /// Used for batch duplicate detection without per-event queries.
    /// </summary>
    Task<HashSet<Guid>> GetExistingEventIdsAsync(IEnumerable<Guid> eventIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of distinct event types from audit events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of distinct user names from audit events (server-side).
    /// </summary>
    Task<List<string>> GetDistinctUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets unique user count matching the predicate (server-side).
    /// </summary>
    Task<int> GetUniqueUserCountAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets event type counts grouped by event type (server-side), matching the predicate.
    /// </summary>
    Task<List<KeyValuePair<string, int>>> GetEventTypeCountsAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets top users by activity count (server-side), matching the predicate.
    /// </summary>
    Task<List<KeyValuePair<string, int>>> GetTopUsersByActivityAsync(
        int take = 10,
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets daily event counts grouped by date and event type (server-side aggregation).
    /// </summary>
    Task<List<(DateTimeOffset Date, string EventType, int Count)>> GetDailyEventCountsAsync(
        DateTimeOffset startDate, DateTimeOffset endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets event counts grouped by user and event type (server-side aggregation).
    /// </summary>
    Task<List<(string User, string EventType, int Count)>> GetUserEventCountsAsync(
        DateTimeOffset startDate, DateTimeOffset endDate, int take = 20,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams audit events matching the predicate, ordered by <c>InsertedDate</c> ascending,
    /// without buffering the full result set. Intended for archival and bulk export where
    /// the event set is too large to materialize into memory.
    /// </summary>
    IAsyncEnumerable<AuditEventEntity> StreamByDateAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>> predicate,
        CancellationToken cancellationToken = default);
}