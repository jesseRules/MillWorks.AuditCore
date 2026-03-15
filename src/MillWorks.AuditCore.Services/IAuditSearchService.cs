using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for searching and retrieving audit events.
/// </summary>
public interface IAuditSearchService
{
    /// <summary>
    /// Searches audit events based on various criteria.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> SearchAuditEventsAsync(AuditSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a paginated list of audit events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<string>> GetDistinctUsersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of distinct users who have performed actions in the audit log.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a list of distinct entity types that have been audited.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<string>> GetDistinctEntityTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches audit events by entity type and optional entity ID.
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="entityId"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> SearchByEntityAsync(string entityType, string? entityId = null, int offset = 0,
        int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches audit events by user and optional date range.
    /// </summary>
    /// <param name="username"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> SearchByUserAsync(string username, DateTimeOffset? startDate = null, DateTimeOffset? endDate = null,
        int offset = 0, int limit = 50, CancellationToken cancellationToken = default);
}