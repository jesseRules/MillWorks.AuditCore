using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for audit services that provide methods to retrieve audit logs and summaries of user and project activities.
/// </summary>
public interface IAuditService
{
    /// <summary>
    /// Gets the audit trail for a specific entity by its name and ID.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogDto>> GetEntityAuditTrailAsync(string entityName, Guid entityId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the activity logs for a specific user, optionally filtered by date and limited to a specified number of entries.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditLogDto>> GetUserActivityAsync(Guid userId, DateTimeOffset? fromDate = null, int take = 50,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Gets a summary of user activity, including the number of actions performed by each user since a specified date.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Dictionary<string, int>> GetActivitySummaryAsync(Guid? userId = null, DateTimeOffset? fromDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Event By Id
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventDto?> GetAuditEventById(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Events
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> GetAuditEvents(int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default);


    /// <summary>
    /// Search Audit Events
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> SearchAuditEvents(AuditSearchRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Events By Date Range
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> GetAuditEventsByDateRange(DateTimeOffset startDate, DateTimeOffset endDate,
        int offset = 0,
        int limit = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Events By User
    /// </summary>
    /// <param name="username"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditEventsResponse> GetAuditEventsByUser(string username, int offset = 0, int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Events By Event Type
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditSummaryResponse> GetAuditSummary(DateTimeOffset? startDate = null, DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Audit Chart Data
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="groupBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<AuditChartData>> GetAuditChartData(DateTimeOffset startDate, DateTimeOffset endDate,
        string groupBy = "day",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Distinct Users
    /// </summary>
    /// <returns></returns>
    Task<List<string?>> GetDistinctUsers(CancellationToken cancellationToken = default);

    /// <summary>
    /// Get Distinct Event Types
    /// </summary>
    /// <returns></returns>
    Task<List<string?>> GetDistinctEventTypes(CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a security event with high-risk content and a warning message.
    /// </summary>
    /// <param name="highRiskContent"></param>
    /// <param name="warning"></param>
    /// <param name="user"></param>
    /// <param name="details"></param>
    /// <param name="additionalInfo"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogSecurityEventAsync(string highRiskContent, string warning, string user, object details, object additionalInfo,
        CancellationToken cancellationToken);
}