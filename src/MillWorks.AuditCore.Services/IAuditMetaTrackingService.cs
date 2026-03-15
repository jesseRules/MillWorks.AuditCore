using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for audit meta-tracking
/// </summary>
public interface IAuditMetaTrackingService
{
    /// <summary>
    /// Logs an audit query with the specified parameters.
    /// </summary>
    /// <param name="queryType"></param>
    /// <param name="queryParameters"></param>
    /// <param name="purpose"></param>
    /// <param name="recordsReturned"></param>
    /// <param name="justification"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogAuditQueryAsync(
        string queryType,
        string queryParameters,
        string purpose,
        int recordsReturned,
        string? justification = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an audit export with the specified parameters.
    /// </summary>
    /// <param name="exportType"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="format"></param>
    /// <param name="purpose"></param>
    /// <param name="approvalReference"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogAuditExportAsync(
        string exportType,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string format,
        string purpose,
        string? approvalReference = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs access to a compliance report with the specified parameters.
    /// </summary>
    /// <param name="standard"></param>
    /// <param name="reportPeriodStart"></param>
    /// <param name="reportPeriodEnd"></param>
    /// <param name="requestedBy"></param>
    /// <param name="purpose"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogComplianceReportAccessAsync(
        ComplianceStandard standard,
        DateTimeOffset reportPeriodStart,
        DateTimeOffset reportPeriodEnd,
        string requestedBy,
        string purpose,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs a tamper detection check with the specified parameters.
    /// </summary>
    /// <param name="checkPassed"></param>
    /// <param name="eventsChecked"></param>
    /// <param name="tamperEventsFound"></param>
    /// <param name="initiatedBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogTamperDetectionCheckAsync(
        bool checkPassed,
        int eventsChecked,
        int tamperEventsFound,
        string initiatedBy,
        CancellationToken cancellationToken = default);
}