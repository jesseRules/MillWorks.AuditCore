using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Tracks access to the audit system itself for compliance
/// Meta-auditing required by compliance standards
/// </summary>
public sealed class AuditMetaTrackingService(
    IAuditLogger auditLogger)
    : IAuditMetaTrackingService
{
    private static readonly string[] _complianceKeywords =
        ["compliance", "audit", "regulatory", "gdpr", "hipaa", "sox", "iso"];

    private static readonly HashSet<string> _sensitiveQueryTypes =
        new(StringComparer.OrdinalIgnoreCase) { "UserActivity", "PHIAccess", "SensitiveData", "SecurityIncident" };

    private static readonly string[] _sensitiveTerms =
        ["executive", "security", "breach", "incident", "violation"];
    /// <summary>
    /// Log when someone queries audit logs (required for HIPAA, SOC2)
    /// </summary>
    public async Task LogAuditQueryAsync(
        string queryType,
        string queryParameters,
        string purpose,
        int recordsReturned,
        string? justification = null,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new AuditEvent
        {
            EventType = $"AuditSystem.Query.{queryType}",
            CustomFields = new Dictionary<string, object?>
            {
                ["QueryType"] = queryType,
                ["QueryParameters"] = queryParameters,
                ["Purpose"] = purpose,
                ["Justification"] = justification,
                ["RecordsReturned"] = recordsReturned,
                ["AccessTime"] = DateTimeOffset.UtcNow,

                // Compliance flags
                ["IsComplianceQuery"] = IsComplianceRelated(purpose),
                ["RequiresJustification"] = RequiresJustification(queryType),
                ["IsSensitiveQuery"] = IsSensitiveQuery(queryParameters)
            }
        };

        await auditLogger.LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Log when someone exports audit data (critical for compliance)
    /// </summary>
    public async Task LogAuditExportAsync(
        string exportType,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        string format,
        string purpose,
        string? approvalReference = null,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new AuditEvent
        {
            EventType = "AuditSystem.Export",
            CustomFields = new Dictionary<string, object?>
            {
                ["ExportType"] = exportType,
                ["DateRangeStart"] = startDate,
                ["DateRangeEnd"] = endDate,
                ["Format"] = format,
                ["Purpose"] = purpose,
                ["ApprovalReference"] = approvalReference,
                ["ExportTime"] = DateTimeOffset.UtcNow,

                // Track for data governance
                ["DataGovernanceFlag"] = true,
                ["RequiresApproval"] = string.IsNullOrEmpty(approvalReference)
            }
        };

        await auditLogger.LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Log when compliance reports are generated
    /// </summary>
    public async Task LogComplianceReportAccessAsync(
        ComplianceStandard standard,
        DateTimeOffset reportPeriodStart,
        DateTimeOffset reportPeriodEnd,
        string requestedBy,
        string purpose,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new AuditEvent
        {
            EventType = "AuditSystem.ComplianceReport",
            CustomFields = new Dictionary<string, object?>
            {
                ["Standard"] = standard.ToString(),
                ["PeriodStart"] = reportPeriodStart,
                ["PeriodEnd"] = reportPeriodEnd,
                ["RequestedBy"] = requestedBy,
                ["Purpose"] = purpose,
                ["GeneratedAt"] = DateTimeOffset.UtcNow
            }
        };

        await auditLogger.LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Log when tamper detection is run
    /// </summary>
    public async Task LogTamperDetectionCheckAsync(
        bool checkPassed,
        int eventsChecked,
        int tamperEventsFound,
        string initiatedBy,
        CancellationToken cancellationToken = default)
    {
        var auditEvent = new AuditEvent
        {
            EventType = "AuditSystem.TamperCheck",
            CustomFields = new Dictionary<string, object?>
            {
                ["CheckPassed"] = checkPassed,
                ["EventsChecked"] = eventsChecked,
                ["TamperEventsFound"] = tamperEventsFound,
                ["InitiatedBy"] = initiatedBy,
                ["CheckTime"] = DateTimeOffset.UtcNow,

                // Alert if tampering detected
                ["RequiresAlert"] = tamperEventsFound > 0,
                ["SecurityIncident"] = tamperEventsFound > 0
            }
        };

        await auditLogger.LogAsync(auditEvent, cancellationToken);
    }

    /// <summary>
    /// Indicates if the purpose is related to compliance
    /// </summary>
    /// <param name="purpose"></param>
    /// <returns></returns>
    private static bool IsComplianceRelated(string? purpose)
    {
        if (string.IsNullOrEmpty(purpose)) return false;

        foreach (string keyword in _complianceKeywords)
        {
            if (purpose.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if the query type requires justification
    /// </summary>
    /// <param name="queryType"></param>
    /// <returns></returns>
    private static bool RequiresJustification(string queryType)
    {
        return _sensitiveQueryTypes.Contains(queryType);
    }

    /// <summary>
    /// Indicates if the query parameters suggest a sensitive query
    /// </summary>
    /// <param name="parameters"></param>
    /// <returns></returns>
    private static bool IsSensitiveQuery(string? parameters)
    {
        if (string.IsNullOrEmpty(parameters)) return false;

        foreach (string term in _sensitiveTerms)
        {
            if (parameters.Contains(term, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}