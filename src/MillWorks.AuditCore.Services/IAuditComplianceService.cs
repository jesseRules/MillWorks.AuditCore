using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for audit compliance service
/// </summary>
public interface IAuditComplianceService
{
    /// <summary>
    /// Generates a compliance report based on the specified standard and date range.
    /// </summary>
    /// <param name="standard"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<ComplianceReport> GenerateComplianceReportAsync(ComplianceStandard standard, DateTimeOffset startDate,
        DateTimeOffset endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Anonymizes user data for compliance with data protection regulations.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AnonymizationResult> AnonymizeUserDataAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports user audit data for compliance purposes.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditExportResult> ExportUserAuditDataAsync(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the retention compliance of the audit data against the configured retention policies.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> ValidateRetentionComplianceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the retention policy to the audit data.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ApplyRetentionPolicyAsync(CancellationToken cancellationToken = default);
}