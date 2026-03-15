using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for generating audit reports and summaries.
/// </summary>
public interface IAuditReportService
{
    /// <summary>
    /// Gets the audit summary for the specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditSummaryResponse> GetAuditSummaryAsync(DateTimeOffset? startDate = null, DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the audit chart data for the specified date range and grouping.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="groupBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<AuditChartData>> GetAuditChartDataAsync(DateTimeOffset startDate, DateTimeOffset endDate,
        string groupBy = "day",
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the activity summary for users, optionally filtered by user ID and date range.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<Dictionary<string, int>> GetActivitySummaryAsync(Guid? userId = null, DateTimeOffset? fromDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the distribution of audit event types within the specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<AuditEventTypeCount>> GetEventTypeDistributionAsync(DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the top users based on audit activity within the specified date range.
    /// </summary>
    /// <param name="count"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<AuditUserCount>> GetTopUsersAsync(int count = 10, DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Generates an audit report for the specified date range and format.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="format"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<byte[]> GenerateAuditReportAsync(DateTimeOffset startDate, DateTimeOffset endDate, string format = "pdf",
        CancellationToken cancellationToken = default);
}