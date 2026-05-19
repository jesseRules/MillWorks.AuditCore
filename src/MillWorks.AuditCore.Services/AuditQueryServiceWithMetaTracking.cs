using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Decorator;

/// <summary>
/// Decorator that adds meta-tracking to audit query service
/// </summary>
public sealed class AuditQueryServiceWithMetaTracking(
    IAuditQueryService inner,
    IAuditMetaTrackingService metaTracking,
    ILogger<AuditQueryServiceWithMetaTracking> logger)
    : IAuditQueryService
{
    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<AuditQueryServiceWithMetaTracking> _logger = logger;

    /// <summary>
    /// Gets the audit trail for a specific entity.
    /// </summary>
    /// <param name="entityName">The entity type name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="maxResults">Maximum number of results to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit trail entries.</returns>
    public async Task<IEnumerable<AuditLogDto>> GetEntityAuditTrailAsync(
        string entityName,
        Guid entityId,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetEntityAuditTrailAsync(entityName, entityId, maxResults, cancellationToken);

        // Log the access
        IEnumerable<AuditLogDto> entityAuditTrailAsync = result as AuditLogDto[] ?? result.ToArray();
        await metaTracking.LogAuditQueryAsync(
            "EntityTrail",
            $"Entity={entityName}, Id={entityId}, MaxResults={maxResults}",
            "Entity History Review",
            entityAuditTrailAsync.Count(), cancellationToken: cancellationToken);

        // Log the number of events retrieved
        _logger.LogInformation(
            "Retrieved {Count} audit events for entity {EntityName} with ID {EntityId}",
            entityAuditTrailAsync.Count(),
            entityName,
            entityId);

        return entityAuditTrailAsync;
    }

    /// <summary>
    /// Gets user activity logs.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetUserActivityAsync(
        Guid userId,
        DateTimeOffset? fromDate = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetUserActivityAsync(userId, fromDate, take, cancellationToken);

        // Log the access - this is sensitive!
        IEnumerable<AuditLogDto> userActivityAsync = result as AuditLogDto[] ?? result.ToArray();
        await metaTracking.LogAuditQueryAsync(
            "UserActivity",
            $"UserId={userId}, From={fromDate}, Limit={take}",
            "User Activity Review",
            userActivityAsync.Count(),
            "Required for investigation", cancellationToken); // Should come from context

        // Log the number of events retrieved
        _logger.LogInformation(
            "Retrieved {Count} user activity events for user {UserId} from {FromDate}",
            userActivityAsync.Count(),
            userId,
            fromDate?.ToString("o") ?? "all time");

        return userActivityAsync;
    }


    /// <summary>
    /// Gets a paginated list of audit events.
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> GetAuditEventsAsync(int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetAuditEventsAsync(offset, limit, cancellationToken);

        await metaTracking.LogAuditQueryAsync(
            "BulkQuery",
            $"Offset={offset}, Limit={limit}",
            "Audit Review",
            result.Items?.Count ?? 0, cancellationToken: cancellationToken);

        // Log the number of events retrieved
        _logger.LogInformation(
            "Retrieved {Count} audit events with offset {Offset} and limit {Limit}",
            result.Items?.Count ?? 0,
            offset,
            limit);

        return result;
    }

    /// <summary>
    /// Gets a single audit event by ID.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventDto?> GetAuditEventByIdAsync(Guid eventId,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetAuditEventByIdAsync(eventId, cancellationToken);

        await metaTracking.LogAuditQueryAsync(
            "SingleEvent",
            $"EventId={eventId}",
            "Event Detail Review",
            result != null ? 1 : 0, cancellationToken: cancellationToken);

        return result;
    }

    /// <summary>
    /// Gets recent activity logs within the specified hours.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetRecentActivityAsync(int hours = 24,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetRecentActivityAsync(hours, cancellationToken);

        IEnumerable<AuditLogDto> recentActivityAsync = result as AuditLogDto[] ?? result.ToArray();
        await metaTracking.LogAuditQueryAsync(
            "RecentActivity",
            $"Hours={hours}",
            "Recent Activity Monitoring",
            recentActivityAsync.Count(), cancellationToken: cancellationToken);

        return recentActivityAsync;
    }

    /// <summary>
    /// Gets grouped audit trail for an entity.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, List<AuditLogDto>>> GetGroupedAuditTrailAsync(string entityName, Guid entityId,
        CancellationToken cancellationToken = default)
    {
        var result = await inner.GetGroupedAuditTrailAsync(entityName, entityId, cancellationToken);

        await metaTracking.LogAuditQueryAsync(
            "GroupedTrail",
            $"Entity={entityName}, Id={entityId}",
            "Grouped History Review",
            result.Values.Sum(static v => v.Count), cancellationToken: cancellationToken);

        return result;
    }
}