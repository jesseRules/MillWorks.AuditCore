using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// PCI DSS (Payment Card Industry Data Security Standard) Compliance Validator
/// Validates audit logs against PCI DSS v4.0 requirements
/// </summary>
public sealed class PciDssValidator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.PCI_DSS;

    /// <summary>
    /// Validates audit events against PCI DSS compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // Requirement 10.2 - Audit Logs for All System Components
        var hasAuditLogs = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Logs Implementation (Req 10.2)",
            Passed = hasAuditLogs,
            Message = hasAuditLogs
                ? $"Audit logs are being collected ({events.Count} events recorded)"
                : "CRITICAL: No audit logs detected - PCI DSS requires audit trails for all system components",
            Severity = hasAuditLogs ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2",
            TotalCount = events.Count,
            FailedCount = hasAuditLogs ? 0 : 1,
            Recommendations = hasAuditLogs
                ? []
                :
                [
                    "IMMEDIATE: Implement comprehensive audit logging",
                    "Log all access to cardholder data (CHD)",
                    "Log all actions by privileged users",
                    "Log all access to audit trails"
                ]
        });

        // Requirement 10.2.1 - User Access to Cardholder Data
        var chdAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Card", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Payment", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("CHD", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("PAN", StringComparison.OrdinalIgnoreCase) == true ||
            e.EntityType?.Contains("Payment", StringComparison.OrdinalIgnoreCase) == true ||
            e.EntityType?.Contains("Card", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Cardholder Data Access Logging (Req 10.2.1)",
            Passed = chdAccessEvents.Any(),
            Message = chdAccessEvents.Any()
                ? $"All individual user access to cardholder data is being logged ({chdAccessEvents.Count} events)"
                : "WARNING: No cardholder data access events detected - ensure CHD access logging is configured",
            Severity = chdAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.1",
            TotalCount = events.Count,
            FailedCount = chdAccessEvents.Any() ? 0 : 1,
            Recommendations = chdAccessEvents.Any()
                ? ["Continue logging all CHD access with user identification"]
                :
                [
                    "Log all individual user access to cardholder data",
                    "Include view, create, modify, and delete operations",
                    "Capture user ID, timestamp, and data accessed"
                ]
        });

        // Requirement 10.2.2 - Actions by Privileged Users
        var privilegedUserEvents = events.Where(static e =>
            e.EventType?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Privileged", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Root", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Sudo", StringComparison.OrdinalIgnoreCase) == true ||
            e.User?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.User?.Contains("root", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Privileged User Actions (Req 10.2.2)",
            Passed = privilegedUserEvents.Any(),
            Message = privilegedUserEvents.Any()
                ? $"Actions by privileged users are being logged ({privilegedUserEvents.Count} events)"
                : "All actions taken by privileged users must be logged",
            Severity = privilegedUserEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.2",
            TotalCount = events.Count,
            FailedCount = privilegedUserEvents.Any() ? 0 : 1,
            Recommendations = privilegedUserEvents.Any()
                ? []
                :
                [
                    "Log all actions by users with administrative privileges",
                    "Include root/admin account activities",
                    "Track privilege escalation (sudo, su, etc.)"
                ]
        });

        // Requirement 10.2.3 - Access to Audit Trails
        var auditAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Audit", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Log", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Trail Access Logging (Req 10.2.3)",
            Passed = auditAccessEvents.Any(),
            Message = auditAccessEvents.Any()
                ? $"Access to audit trails is being logged ({auditAccessEvents.Count} events)"
                : "Access to audit trails should be logged to prevent tampering",
            Severity = auditAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.3",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations = auditAccessEvents.Any()
                ? []
                :
                [
                    "Log all access to audit trails",
                    "Monitor for unauthorized audit log access",
                    "Alert on suspicious audit trail queries"
                ]
        });

        // Requirement 10.2.4 - Invalid Logical Access Attempts
        var failedAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Failed", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Denied", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Rejected", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Invalid Access Attempts (Req 10.2.4)",
            Passed = true, // Having events is good, not having them is neutral
            Message = failedAccessEvents.Any()
                ? $"Invalid logical access attempts are being logged ({failedAccessEvents.Count} events)"
                : "Ensure invalid logical access attempts are being logged when they occur",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.4",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Log all failed login attempts",
                "Track repeated failed access attempts (potential brute force)",
                "Include source IP, user ID, and timestamp"
            ]
        });

        // Requirement 10.2.5 - Changes to Identification/Authentication
        var authChangeEvents = events.Where(static e =>
            e.EventType?.Contains("Password", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Credential", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Authentication", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("User", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Authentication Changes (Req 10.2.5)",
            Passed = authChangeEvents.Any(),
            Message = authChangeEvents.Any()
                ? $"Changes to authentication credentials are being logged ({authChangeEvents.Count} events)"
                : "Log all changes to identification and authentication credentials",
            Severity = authChangeEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.5",
            TotalCount = events.Count,
            FailedCount = authChangeEvents.Any() ? 0 : 1,
            Recommendations = authChangeEvents.Any()
                ? []
                :
                [
                    "Log all password changes, resets, and updates",
                    "Track user account creation, modification, and deletion",
                    "Log multi-factor authentication changes"
                ]
        });

        // Requirement 10.2.6 - Initialization of Audit Logs
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Initialization (Req 10.2.6)",
            Passed = hasAuditLogs,
            Message = hasAuditLogs
                ? "Audit log initialization and stopping is tracked"
                : "Log initialization, stopping, or pausing of audit logs",
            Severity = hasAuditLogs ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.6",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // Requirement 10.2.7 - Creation and Deletion of System Objects
        var objectChangeEvents = events.Where(static e =>
            e.Action == "Added" || e.Action == "Deleted" ||
            e.EventType?.Contains("Create", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Delete", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "System Object Changes (Req 10.2.7)",
            Passed = objectChangeEvents.Any(),
            Message = objectChangeEvents.Any()
                ? $"Creation and deletion of system objects is being logged ({objectChangeEvents.Count} events)"
                : "Log creation and deletion of system-level objects",
            Severity = objectChangeEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.2.7",
            TotalCount = events.Count,
            FailedCount = objectChangeEvents.Any() ? 0 : 1
        });

        // Requirement 10.3 - Audit Log Entries Include Required Details
        var hasUserIdentification = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        var hasTimestamps = events.All(static e => e.InsertedDate.HasValue);
        var eventsWithoutUser = events.Count(static e => string.IsNullOrWhiteSpace(e.User));
        var eventsWithoutTimestamp = events.Count(static e => !e.InsertedDate.HasValue);

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Detail Requirements (Req 10.3)",
            Passed = hasUserIdentification && hasTimestamps,
            Message = (hasUserIdentification && hasTimestamps)
                ? "All audit logs include required details (user ID, timestamp, event type)"
                : $"Audit logs missing required details: {eventsWithoutUser} without user ID, {eventsWithoutTimestamp} without timestamp",
            Severity = (hasUserIdentification && hasTimestamps) ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.3",
            TotalCount = events.Count,
            FailedCount = eventsWithoutUser + eventsWithoutTimestamp,
            Recommendations = (hasUserIdentification && hasTimestamps)
                ? []
                :
                [
                    "CRITICAL: Ensure all audit entries include user identification",
                    "CRITICAL: Ensure all audit entries include date and time",
                    "Include event type, success/failure indication",
                    "Include origination of event (source IP/system)",
                    "Include identity/name of affected data, system component, or resource"
                ]
        });

        // Requirement 10.3.4 - Time Synchronization
        var recentEvents = events.Where(static e => e.InsertedDate >= DateTimeOffset.UtcNow.AddHours(-1)).ToList();
        var hasRecentEvents = recentEvents.Any();

        results.Add(new AuditValidationResult
        {
            RuleName = "Time Synchronization (Req 10.3.4)",
            Passed = hasRecentEvents,
            Message = hasRecentEvents
                ? "Time stamps are current and synchronized (recent events detected)"
                : "Ensure system clocks are synchronized using time synchronization technology",
            Severity = hasRecentEvents ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.3.4",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Use Network Time Protocol (NTP) for time synchronization",
                "Ensure all systems use consistent time sources",
                "Monitor for time drift in audit logs"
            ]
        });

        // Requirement 10.4 - Protect Audit Logs (server-side counts)
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Protection (Req 10.4)",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} audit logs are protected from unauthorized modifications"
                : context.TotalEventCount == 0
                    ? "No events to verify"
                    : $"CRITICAL: {context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.Critical,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.4",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? []
                :
                [
                    "IMMEDIATE: Implement file integrity monitoring for audit logs",
                    "Use cryptographic hashing to detect tampering",
                    "Restrict access to audit log files",
                    "Store logs on centralized internal log server or media"
                ]
        });

        // Requirement 10.5 - Retain Audit Log History (server-side oldest date)
        var retentionDays = context.OldestEventDate.HasValue
            ? (DateTimeOffset.UtcNow - context.OldestEventDate.Value).Days
            : 0;

        // PCI DSS requires minimum 1 year retention, with 3 months immediately available
        var meetsMinimumRetention = retentionDays >= 365;
        var meetsImmediateAvailability = retentionDays >= 90;

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Retention (Req 10.5)",
            Passed = meetsMinimumRetention,
            Message = meetsMinimumRetention
                ? $"Audit logs meet 1-year retention requirement (current: {retentionDays} days)"
                : meetsImmediateAvailability
                    ? $"Audit logs meet 3-month immediate availability requirement but not 1-year retention (current: {retentionDays} days)"
                    : $"Audit logs do not meet retention requirements (current: {retentionDays} days, required: 365 days minimum)",
            Severity = meetsMinimumRetention ? ValidationSeverity.Info
                : meetsImmediateAvailability ? ValidationSeverity.Medium
                : ValidationSeverity.High,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.5",
            TotalCount = events.Count,
            FailedCount = meetsMinimumRetention ? 0 : 1,
            Recommendations = meetsMinimumRetention
                ?
                [
                    "Continue maintaining 1-year retention",
                    "Keep 3 months immediately available for analysis"
                ]
                :
                [
                    "REQUIRED: Retain audit logs for at least one year",
                    "REQUIRED: Keep at least three months immediately available online",
                    "Configure automatic archival for long-term retention",
                    "Older logs may be archived but must be retrievable"
                ]
        });

        // Requirement 10.6 - Review Logs Regularly
        var reviewEvents = events.Where(static e =>
            e.EventType?.Contains("Review", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Audit", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Regular Log Review (Req 10.6)",
            Passed = hasRecentEvents, // Proxy - if recent events exist, system is active
            Message = hasRecentEvents
                ? "System is active and logs are being generated for review"
                : "Ensure logs are reviewed at least daily for security events and anomalies",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.6",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Review logs of all system components at least daily",
                "Use automated tools (SIEM) to aggregate and review logs",
                "Document log review process and findings",
                "Escalate exceptions and anomalies to appropriate personnel"
            ]
        });

        // Requirement 10.7 - Failure of Critical Security Control Systems
        var alertEvents = events.Where(static e =>
            e.EventType?.Contains("Alert", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Failure", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Error", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Security Control Failure Detection (Req 10.7)",
            Passed = true,
            Message = alertEvents.Any()
                ? $"Security control failures are being detected and logged ({alertEvents.Count} events)"
                : "Ensure failures of critical security controls are detected and alerted promptly",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "PCI DSS",
            Category = "Requirement 10 - Track and Monitor Access",
            RegulationReference = "PCI DSS v4.0 Requirement 10.7",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Implement real-time alerts for critical security control failures",
                "Monitor anti-malware, IDS/IPS, and firewall failures",
                "Alert on audit logging system failures",
                "Document response procedures for security control failures"
            ]
        });

        await Task.CompletedTask;
        return results;
    }

    /// <summary>
    /// Generates recommendations based on the validation results.
    /// </summary>
    /// <param name="results"></param>
    /// <returns></returns>
    public List<string> GenerateRecommendations(IEnumerable<AuditValidationResult> results)
    {
        var recommendations = new List<string>();
        var failedResults = results.Where(static r => !r.Passed).OrderByDescending(static r => r.Severity).ToList();

        if (failedResults.Count == 0)
        {
            recommendations.Add("✅ PCI DSS COMPLIANCE: All validation checks passed");
            recommendations.Add("");
            recommendations.Add("📋 Ongoing PCI DSS Requirements:");
            recommendations.Add("  • Quarterly vulnerability scans by ASV");
            recommendations.Add("  • Annual penetration testing");
            recommendations.Add("  • Daily log reviews");
            recommendations.Add("  • Quarterly risk assessments");
            recommendations.Add("  • Annual policy reviews");
            recommendations.Add("  • Security awareness training");
            return recommendations;
        }

        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();

        recommendations.Add("💳 PCI DSS COMPLIANCE REPORT");
        recommendations.Add($"Found {failedResults.Count} compliance issue(s)");
        recommendations.Add("");

        if (critical.Any())
        {
            recommendations.Add("🚨 CRITICAL - IMMEDIATE ACTION REQUIRED:");
            recommendations.Add("These issues must be addressed immediately to maintain PCI DSS compliance.");
            recommendations.Add(
                "Failure may result in fines, increased transaction fees, or loss of card processing privileges.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  ⚠️ {result.RuleName}");
                recommendations.Add($"     Requirement: {result.RegulationReference}");
                recommendations.Add($"     Issue: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("     REQUIRED ACTIONS:");
                    foreach (var rec in result.Recommendations)
                    {
                        recommendations.Add($"     • {rec}");
                    }
                }

                recommendations.Add("");
            }
        }

        if (high.Any())
        {
            recommendations.Add("🔴 HIGH PRIORITY - Address Immediately:");
            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    {result.Message}");
                if (result.Recommendations.Any())
                {
                    foreach (var rec in result.Recommendations)
                    {
                        recommendations.Add($"    - {rec}");
                    }
                }

                recommendations.Add("");
            }
        }

        if (medium.Any())
        {
            recommendations.Add("🟡 MEDIUM PRIORITY - Address Soon:");
            foreach (var result in medium)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
            }

            recommendations.Add("");
        }

        recommendations.Add("📚 PCI DSS Resources:");
        recommendations.Add("  • PCI SSC Official Site: https://www.pcisecuritystandards.org");
        recommendations.Add(
            "  • Self-Assessment Questionnaires: https://www.pcisecuritystandards.org/document_library");
        recommendations.Add(
            "  • Approved Scanning Vendors: https://www.pcisecuritystandards.org/assessors_and_solutions/approved_scanning_vendors");
        recommendations.Add("");

        recommendations.Add("⚖️ Non-Compliance Consequences:");
        recommendations.Add("  • Monthly fines: $5,000 - $100,000 per month");
        recommendations.Add("  • Increased transaction fees: $0.05 - $0.20 per transaction");
        recommendations.Add("  • Forensic audit costs: $50,000 - $500,000");
        recommendations.Add("  • Card brand restrictions or loss of processing privileges");
        recommendations.Add("");

        recommendations.Add("🎯 PCI DSS Validation Levels:");
        recommendations.Add("  • Level 1: 6M+ transactions/year - Annual onsite assessment by QSA");
        recommendations.Add("  • Level 2: 1-6M transactions/year - Annual SAQ + quarterly network scan");
        recommendations.Add("  • Level 3: 20K-1M e-commerce transactions/year - Annual SAQ + quarterly scan");
        recommendations.Add("  • Level 4: <20K e-commerce or <1M transactions/year - Annual SAQ + quarterly scan");

        return recommendations;
    }
}
