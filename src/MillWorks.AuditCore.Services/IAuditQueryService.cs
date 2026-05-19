using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for querying audit logs and events
/// </summary>
public interface IAuditQueryService
{
    /// <summary>
    /// Gets the audit trail for a specific entity type and ID.
    /// </summary>
    /// <param name="entityName">The entity type name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="maxResults">Maximum number of results to return. Clamped to QueryLimits.MaxPageSize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit trail entries, ordered by most recent first.</returns>
    Task<IEnumerable<AuditLogDto>> GetEntityAuditTrailAsync(string entityName, Guid entityId,
        int maxResults = 1000, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the audit trail for a specific entity type and ID.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogDto>> GetUserActivityAsync(Guid userId, DateTimeOffset? fromDate = null, int take = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a paginated list of audit events.
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> GetAuditEventsAsync(int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific audit event by its ID.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventDto?> GetAuditEventByIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets recent activity from the audit log.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogDto>>
        GetRecentActivityAsync(int hours = 24, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a grouped audit trail for a specific entity.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Dictionary<string, List<AuditLogDto>>> GetGroupedAuditTrailAsync(string entityName, Guid entityId,
        CancellationToken cancellationToken = default);
}