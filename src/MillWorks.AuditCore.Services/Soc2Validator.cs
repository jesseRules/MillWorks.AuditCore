using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// SOC 2 (Service Organization Control 2) Compliance Validator
/// Validates audit logs against SOC 2 Trust Service Criteria
/// </summary>
public sealed class Soc2Validator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.SOC2;

    /// <summary>
    /// Validates audit events against SOC2 compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // CC6.1 - Logical and Physical Access Controls
        var hasAccessControls = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Access Control Logging (CC6.1)",
            Passed = hasAccessControls,
            Message = hasAccessControls
                ? $"Logical access controls are being logged ({events.Count} events)"
                : "No access control logging detected - SOC 2 requires comprehensive access monitoring",
            Severity = hasAccessControls ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Logical and Physical Access",
            RegulationReference = "CC6.1",
            TotalCount = events.Count,
            FailedCount = hasAccessControls ? 0 : 1,
            Recommendations = hasAccessControls
                ? []
                :
                [
                    "Implement comprehensive access logging",
                    "Log both successful and failed access attempts",
                    "Include user identification and timestamps"
                ]
        });

        // CC6.2 - Prior to Access, Identify and Authenticate Users
        var authenticationEvents = events.Where(static e =>
            e.EventType?.Contains("Login", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Authentication", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SignIn", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Authentication Logging (CC6.2)",
            Passed = authenticationEvents.Any(),
            Message = authenticationEvents.Any()
                ? $"User authentication is being logged ({authenticationEvents.Count} authentication events)"
                : "Authentication events should be logged to verify identity prior to access",
            Severity = authenticationEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Logical and Physical Access",
            RegulationReference = "CC6.2",
            TotalCount = events.Count,
            FailedCount = authenticationEvents.Any() ? 0 : 1,
            Recommendations = authenticationEvents.Any()
                ? []
                :
                [
                    "Log all authentication attempts",
                    "Track multi-factor authentication usage",
                    "Monitor for suspicious authentication patterns"
                ]
        });

        // CC6.3 - Manage System Accounts
        var accountManagementEvents = events.Where(static e =>
            e.EventType?.Contains("User", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Account", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Permission", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Role", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Account Management Logging (CC6.3)",
            Passed = accountManagementEvents.Any(),
            Message = accountManagementEvents.Any()
                ? $"Account provisioning and changes are being logged ({accountManagementEvents.Count} events)"
                : "Account creation, modification, and deletion should be logged",
            Severity = accountManagementEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Logical and Physical Access",
            RegulationReference = "CC6.3",
            TotalCount = events.Count,
            FailedCount = accountManagementEvents.Any() ? 0 : 1,
            Recommendations = accountManagementEvents.Any()
                ? []
                :
                [
                    "Log user account provisioning and deprovisioning",
                    "Track permission and role changes",
                    "Document approval process for access requests"
                ]
        });

        // CC6.6 - Implement Logical Access Security Measures
        var hasUserIdentification = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        var eventsWithoutUser = events.Count(static e => string.IsNullOrWhiteSpace(e.User));

        results.Add(new AuditValidationResult
        {
            RuleName = "User Identification (CC6.6)",
            Passed = hasUserIdentification,
            Message = hasUserIdentification
                ? "All audit events include user identification"
                : $"{eventsWithoutUser} events lack user identification - required for accountability",
            Severity = hasUserIdentification ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Logical and Physical Access",
            RegulationReference = "CC6.6",
            TotalCount = events.Count,
            FailedCount = eventsWithoutUser,
            Recommendations = hasUserIdentification
                ? []
                :
                [
                    "Ensure all audit events include authenticated user identification",
                    "Implement unique user IDs for accountability"
                ]
        });

        // CC7.2 - Detect and Respond to Security Events
        var securityEvents = events.Where(static e =>
            e.EventType?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Incident", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Threat", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Breach", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Alert", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Security Event Detection (CC7.2)",
            Passed = true, // Having no security events is acceptable
            Message = securityEvents.Any()
                ? $"Security events and incidents are being detected and logged ({securityEvents.Count} events)"
                : "Ensure security event detection and logging is configured",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - System Operations",
            RegulationReference = "CC7.2",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Implement automated security monitoring",
                "Configure real-time alerts for security events",
                "Document incident response procedures",
                "Conduct regular security event reviews"
            ]
        });

        // CC7.3 - Evaluate Security Events
        var recentEvents = events.Where(static e => e.InsertedDate >= DateTimeOffset.UtcNow.AddDays(-30)).ToList();
        results.Add(new AuditValidationResult
        {
            RuleName = "Security Event Evaluation (CC7.3)",
            Passed = recentEvents.Any(),
            Message = recentEvents.Any()
                ? $"Recent audit events indicate active monitoring ({recentEvents.Count} events in last 30 days)"
                : "Regular evaluation of security events is required",
            Severity = recentEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - System Operations",
            RegulationReference = "CC7.3",
            TotalCount = events.Count,
            FailedCount = recentEvents.Any() ? 0 : 1,
            Recommendations =
            [
                "Review audit logs regularly (at least monthly)",
                "Investigate anomalies and unusual patterns",
                "Document review findings and actions taken"
            ]
        });

        // CC7.4 - Respond to Security Incidents
        var incidentResponseEvents = events.Where(static e =>
            e.EventType?.Contains("Response", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Remediation", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Investigation", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Incident Response Tracking (CC7.4)",
            Passed = incidentResponseEvents.Any() || !securityEvents.Any(),
            Message = incidentResponseEvents.Any()
                ? $"Security incident responses are being documented ({incidentResponseEvents.Count} events)"
                : "Ensure security incident responses are logged and tracked",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - System Operations",
            RegulationReference = "CC7.4",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // CC8.1 - Authorize, Design, Develop, Configure Changes
        var changeManagementEvents = events.Where(static e =>
            e.EventType?.Contains("Change", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Deploy", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Release", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Configuration", StringComparison.OrdinalIgnoreCase) == true ||
            e.Action == "Modified").ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Change Management Logging (CC8.1)",
            Passed = changeManagementEvents.Any(),
            Message = changeManagementEvents.Any()
                ? $"System changes are being logged and tracked ({changeManagementEvents.Count} change events)"
                : "System changes should be logged for change management compliance",
            Severity = changeManagementEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Change Management",
            RegulationReference = "CC8.1",
            TotalCount = events.Count,
            FailedCount = changeManagementEvents.Any() ? 0 : 1,
            Recommendations = changeManagementEvents.Any()
                ? []
                :
                [
                    "Log all system and configuration changes",
                    "Track change approvals and authorizations",
                    "Document change testing and validation"
                ]
        });

        // CC6.7 - Restrict Privileged Access
        var privilegedAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Privileged", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Root", StringComparison.OrdinalIgnoreCase) == true ||
            e.User?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Privileged Access Monitoring (CC6.7)",
            Passed = privilegedAccessEvents.Any(),
            Message = privilegedAccessEvents.Any()
                ? $"Privileged access is being monitored ({privilegedAccessEvents.Count} privileged events)"
                : "Privileged and administrative access should be logged and monitored",
            Severity = privilegedAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Logical and Physical Access",
            RegulationReference = "CC6.7",
            TotalCount = events.Count,
            FailedCount = privilegedAccessEvents.Any() ? 0 : 1,
            Recommendations = privilegedAccessEvents.Any()
                ? []
                :
                [
                    "Log all privileged user activities",
                    "Monitor for privilege escalation",
                    "Review privileged access regularly",
                    "Implement just-in-time privileged access where possible"
                ]
        });

        // A1.2 - Availability Processing Integrity (if Type 2 report includes Availability)
        var systemHealthEvents = events.Where(static e =>
            e.EventType?.Contains("Health", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Performance", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Availability", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Uptime", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "System Availability Monitoring (A1.2)",
            Passed = systemHealthEvents.Any(),
            Message = systemHealthEvents.Any()
                ? $"System availability and performance are being monitored ({systemHealthEvents.Count} events)"
                : "Consider logging system availability and performance metrics for Type 2 reports",
            Severity = systemHealthEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Low,
            ComplianceStandard = "SOC 2",
            Category = "Availability Criteria",
            RegulationReference = "A1.2 (Type 2)",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // C1.2 - Confidentiality (if Type 2 report includes Confidentiality)
        var dataAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Data", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Confidential", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Sensitive", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Export", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Confidential Data Access (C1.2)",
            Passed = dataAccessEvents.Any(),
            Message = dataAccessEvents.Any()
                ? $"Confidential data access is being logged ({dataAccessEvents.Count} data access events)"
                : "Consider logging confidential data access for Type 2 reports with Confidentiality criteria",
            Severity = dataAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Low,
            ComplianceStandard = "SOC 2",
            Category = "Confidentiality Criteria",
            RegulationReference = "C1.2 (Type 2)",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // Audit Log Retention
        var oldestEvent = events.MinBy(static e => e.InsertedDate);
        var retentionDays = oldestEvent?.InsertedDate.HasValue == true
            ? (DateTimeOffset.UtcNow - oldestEvent.InsertedDate.Value).Days
            : 0;

        // SOC 2 typically requires retention for audit period (usually 12 months)
        var meetsRetention = retentionDays >= 365;

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Retention",
            Passed = meetsRetention,
            Message = meetsRetention
                ? $"Audit logs meet retention requirements (current: {retentionDays} days)"
                : $"Audit log retention may be insufficient (current: {retentionDays} days, recommended: 365+ days for SOC 2)",
            Severity = meetsRetention ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - Monitoring",
            RegulationReference = "CC7.2",
            TotalCount = events.Count,
            FailedCount = meetsRetention ? 0 : 1,
            Recommendations = meetsRetention
                ? []
                :
                [
                    "Maintain audit logs for at least 12 months for SOC 2 examination period",
                    "Configure automatic archival for long-term retention",
                    "Document retention policies in system description"
                ]
        });

        // Audit Log Integrity (server-side counts)
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Integrity Protection",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} audit logs have integrity protection"
                : context.TotalEventCount == 0
                    ? "No events to verify"
                    : $"{context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.High,
            ComplianceStandard = "SOC 2",
            Category = "Common Criteria - System Operations",
            RegulationReference = "CC7.2",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? []
                :
                [
                    "Implement tamper detection for audit logs",
                    "Use cryptographic hashing to ensure integrity",
                    "Restrict audit log modification to authorized personnel only"
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
            recommendations.Add("✅ SOC 2 COMPLIANCE: All validation checks passed");
            recommendations.Add("");
            recommendations.Add("📋 SOC 2 Type 2 Ongoing Requirements:");
            recommendations.Add("  • Maintain controls consistently over examination period (6-12 months)");
            recommendations.Add("  • Document all policy and procedure changes");
            recommendations.Add("  • Conduct regular control testing");
            recommendations.Add("  • Review and update risk assessments");
            recommendations.Add("  • Provide evidence of control effectiveness");
            return recommendations;
        }

        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        recommendations.Add("🛡️ SOC 2 COMPLIANCE REPORT");
        recommendations.Add($"Found {failedResults.Count} area(s) requiring attention");
        recommendations.Add("");

        if (critical.Any())
        {
            recommendations.Add("🚨 CRITICAL - Common Criteria Gaps:");
            recommendations.Add("These are fundamental trust service criteria that must be addressed.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  ⚠️ {result.RuleName}");
                recommendations.Add($"     Control: {result.RegulationReference}");
                recommendations.Add($"     Issue: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("     Actions Required:");
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
            recommendations.Add("🔴 HIGH PRIORITY - Address Before Audit:");
            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.AddRange(result.Recommendations.Select(static rec => $"    - {rec}"));
                }

                recommendations.Add("");
            }
        }

        if (medium.Any())
        {
            recommendations.Add("🟡 MEDIUM PRIORITY - Enhancement Opportunities:");
            recommendations.AddRange(medium.Select(static result => $"  • {result.RuleName}: {result.Message}"));

            recommendations.Add("");
        }

        if (low.Any())
        {
            recommendations.Add("🔵 LOW PRIORITY - Type 2 Additional Criteria:");
            recommendations.AddRange(low.Select(static result => $"  • {result.RuleName}: {result.Message}"));

            recommendations.Add("");
        }

        recommendations.Add("📚 SOC 2 Trust Service Categories:");
        recommendations.Add("  • Security (CC) - Always required");
        recommendations.Add("  • Availability (A) - Optional, system availability commitments");
        recommendations.Add("  • Processing Integrity (PI) - Optional, accurate/timely processing");
        recommendations.Add("  • Confidentiality (C) - Optional, confidential information protection");
        recommendations.Add("  • Privacy (P) - Optional, personal information handling");
        recommendations.Add("");

        recommendations.Add("🎯 SOC 2 Report Types:");
        recommendations.Add("  • Type 1: Point-in-time evaluation of control design");
        recommendations.Add("  • Type 2: Operating effectiveness over period (6-12 months)");
        recommendations.Add("");

        recommendations.Add("📊 Audit Preparation:");
        recommendations.Add("  • Select appropriate Trust Service Categories");
        recommendations.Add("  • Document system description and boundaries");
        recommendations.Add("  • Maintain evidence of control operation");
        recommendations.Add("  • Conduct internal control testing");
        recommendations.Add("  • Address any control deficiencies");

        return recommendations;
    }
}