using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// GDPR (General Data Protection Regulation) Compliance Validator
/// Validates audit logs against EU GDPR requirements
/// </summary>
public sealed class GdprValidator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.GDPR;

    /// <summary>
    /// Validates a list of audit events against GDPR compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        // Short-circuit if this standard is not enabled
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // Article 30 - Records of processing activities
        var hasProcessingRecords = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Records of Processing (Article 30)",
            Passed = hasProcessingRecords,
            Message = hasProcessingRecords
                ? $"Processing activities are being recorded ({events.Count} events)"
                : "No processing records found - GDPR Article 30 requires maintaining records",
            Severity = hasProcessingRecords ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "GDPR",
            Category = "Record Keeping",
            RegulationReference = "GDPR Article 30",
            TotalCount = events.Count,
            FailedCount = hasProcessingRecords ? 0 : 1,
            Recommendations = hasProcessingRecords
                ? []
                :
                [
                    "Implement comprehensive audit logging for all personal data processing",
                    "Ensure all data access and modifications are tracked"
                ]
        });

        // Article 7 - User Consent Tracking
        var consentEvents = events.Where(static e =>
            e.EventType?.Contains("Consent", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "User Consent Tracking (Article 7)",
            Passed = consentEvents.Any(),
            Message = consentEvents.Any()
                ? $"User consent is being tracked ({consentEvents.Count} consent events)"
                : "No consent tracking found - GDPR requires proof of consent",
            Severity = consentEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "GDPR",
            Category = "Lawful Basis",
            RegulationReference = "GDPR Article 7",
            TotalCount = events.Count,
            FailedCount = consentEvents.Any() ? 0 : 1,
            Recommendations = consentEvents.Any()
                ? []
                :
                [
                    "Implement consent tracking for all data collection",
                    "Log consent grants, withdrawals, and modifications",
                    "Include timestamp and specifics of what was consented to"
                ]
        });

        // Article 15 - Right of Access - Data Access Logging
        var dataAccessEvents = events.Where(static e =>
            e.EventType?.Contains("DataAccess", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("PersonalData", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("View", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Data Access Logging (Article 15)",
            Passed = dataAccessEvents.Any(),
            Message = dataAccessEvents.Any()
                ? $"Personal data access is being logged ({dataAccessEvents.Count} access events)"
                : "All personal data access must be logged per GDPR Article 15",
            Severity = dataAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "GDPR",
            Category = "Data Subject Rights",
            RegulationReference = "GDPR Article 15",
            TotalCount = events.Count,
            FailedCount = dataAccessEvents.Any() ? 0 : 1,
            Recommendations = dataAccessEvents.Any()
                ? []
                :
                [
                    "Log all access to personal data",
                    "Include who accessed, when, and what data was accessed",
                    "Required to respond to subject access requests (SAR)"
                ]
        });

        // Article 17 - Right to Erasure (Right to be Forgotten)
        var deletionEvents = events.Where(static e =>
            e.EventType?.Contains("Delete", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Erasure", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Anonymize", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Right to Erasure Tracking (Article 17)",
            Passed = deletionEvents.Any(),
            Message = deletionEvents.Any()
                ? $"Data deletion/erasure requests are being tracked ({deletionEvents.Count} events)"
                : "GDPR requires tracking data deletion and erasure requests",
            Severity = deletionEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "GDPR",
            Category = "Data Subject Rights",
            RegulationReference = "GDPR Article 17",
            TotalCount = events.Count,
            FailedCount = deletionEvents.Any() ? 0 : 1,
            Recommendations = deletionEvents.Any()
                ? []
                :
                [
                    "Log all data deletion and erasure requests",
                    "Track completion of right to be forgotten requests",
                    "Maintain proof of deletion (but not the deleted data itself)"
                ]
        });

        // Article 20 - Right to Data Portability
        var exportEvents = events.Where(static e =>
            e.EventType?.Contains("Export", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Download", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Portability", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Data Portability Tracking (Article 20)",
            Passed = true, // Optional feature, no failure
            Message = exportEvents.Any()
                ? $"Data portability requests are being tracked ({exportEvents.Count} export events)"
                : "Consider implementing data portability request tracking",
            Severity = exportEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Low,
            ComplianceStandard = "GDPR",
            Category = "Data Subject Rights",
            RegulationReference = "GDPR Article 20",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // Article 5(1)(e) - Data Retention Compliance
        // Use server-side oldest date, not sample (sample is recency-biased)
        var retentionDays = context.OldestEventDate.HasValue
            ? (DateTimeOffset.UtcNow - context.OldestEventDate.Value).Days
            : 0;

        // GDPR doesn't specify a maximum, but data should not be kept longer than necessary
        // Most organizations use 2-7 years depending on legal requirements
        var maxRetentionDays = 2555; // 7 years - common maximum
        var retentionCompliant = retentionDays <= maxRetentionDays;

        results.Add(new AuditValidationResult
        {
            RuleName = "Data Retention Compliance (Article 5)",
            Passed = retentionCompliant,
            Message = retentionCompliant
                ? $"Current retention: {retentionDays} days (within {maxRetentionDays} day limit)"
                : $"Data retained for {retentionDays} days exceeds recommended {maxRetentionDays} days",
            Severity = retentionCompliant ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "GDPR",
            Category = "Data Minimization",
            RegulationReference = "GDPR Article 5(1)(e)",
            TotalCount = events.Count,
            FailedCount = retentionCompliant ? 0 : 1,
            Recommendations = retentionCompliant
                ?
                [
                    "Review retention policies annually",
                    "Delete or anonymize data no longer needed"
                ]
                :
                [
                    "Implement automatic data deletion after retention period",
                    "Review and document legitimate reasons for extended retention",
                    "Consider archiving old data with anonymization"
                ]
        });

        // Article 32 - Security of Processing - Access Control
        var hasUserInformation = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        results.Add(new AuditValidationResult
        {
            RuleName = "User Identification (Article 32)",
            Passed = events.Count > 0 && hasUserInformation,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate user identification"
                : hasUserInformation
                    ? "All audit events include user identification"
                    : "Some events lack user identification - required for security and accountability",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : hasUserInformation ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "GDPR",
            Category = "Security Measures",
            RegulationReference = "GDPR Article 32",
            TotalCount = events.Count,
            FailedCount = events.Count(static e => string.IsNullOrWhiteSpace(e.User)),
            Recommendations = hasUserInformation
                ? []
                :
                [
                    "Ensure all audit events include authenticated user information",
                    "Implement proper user identification in audit logging"
                ]
        });

        // Article 33 - Breach Notification Tracking
        var breachEvents = events.Where(static e =>
            e.EventType?.Contains("Breach", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Incident", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Security Breach Tracking (Article 33)",
            Passed = true, // Having no breaches is good
            Message = breachEvents.Any()
                ? $"Security incidents are being tracked ({breachEvents.Count} events) - ensure 72-hour reporting"
                : "No security breach events logged (good, but ensure logging is configured)",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "GDPR",
            Category = "Breach Notification",
            RegulationReference = "GDPR Article 33",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Ensure security breach detection and logging is configured",
                "Document breach notification procedures (72-hour requirement)",
                "Train staff on breach identification and reporting"
            ]
        });

        // Article 35 - Data Protection Impact Assessment (DPIA)
        var dpiaEvents = events.Where(static e =>
            e.EventType?.Contains("DPIA", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Assessment", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("RiskAssessment", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "DPIA Documentation (Article 35)",
            Passed = dpiaEvents.Any(),
            Message = dpiaEvents.Any()
                ? $"DPIA activities are being tracked ({dpiaEvents.Count} events)"
                : "Consider tracking DPIA activities for high-risk processing",
            Severity = dpiaEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Low,
            ComplianceStandard = "GDPR",
            Category = "Risk Management",
            RegulationReference = "GDPR Article 35",
            TotalCount = events.Count,
            FailedCount = 0
        });

        // Overall security - Audit log integrity (server-side counts)
        // All events must have integrity protection when tamper detection is enabled
        var allProtected = context is { TotalEventCount: > 0, UnprotectedEventCount: 0 };
        var hasAnyProtection = context.TotalEventCount > context.UnprotectedEventCount;
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Log Integrity Protection",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} audit events are protected with integrity controls"
                : context.TotalEventCount == 0
                    ? "No events to verify - insufficient data"
                    : hasAnyProtection
                        ? $"{context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection"
                        : "No audit logs have integrity protection - implement tamper detection",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.High,
            ComplianceStandard = "GDPR",
            Category = "Security Measures",
            RegulationReference = "GDPR Article 32",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? []
                :
                [
                    "Enable tamper detection to protect audit log integrity",
                    "Implement cryptographic measures to detect unauthorized modifications"
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
            recommendations.Add("✅ GDPR: All validation checks passed");
            recommendations.Add("📋 Continue monitoring compliance and review annually");
            return recommendations;
        }

        // Group by severity
        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        // Add summary header
        recommendations.Add($"📊 GDPR Compliance Summary: {failedResults.Count} issue(s) found");
        recommendations.Add("");

        if (critical.Any())
        {
            recommendations.Add("⚠️ CRITICAL - IMMEDIATE ACTION REQUIRED:");
            recommendations.Add(
                "These issues represent significant compliance gaps that must be addressed immediately.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  🔴 {result.RuleName}");
                recommendations.Add($"     Issue: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("     Actions:");
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
            recommendations.Add("🔴 HIGH PRIORITY - Address Within 30 Days:");
            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName}");
                recommendations.Add($"    Problem: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("    Solutions:");
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
            recommendations.Add("🟡 MEDIUM PRIORITY - Address Within 90 Days:");
            foreach (var result in medium)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
                if (result.Recommendations.Any())
                {
                    foreach (var rec in result.Recommendations)
                    {
                        recommendations.Add($"    - {rec}");
                    }
                }
            }

            recommendations.Add("");
        }

        if (low.Any())
        {
            recommendations.Add("🔵 LOW PRIORITY - Consider Implementing:");
            foreach (var result in low)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
            }

            recommendations.Add("");
        }

        // Add general GDPR guidance
        recommendations.Add("📚 General GDPR Recommendations:");
        recommendations.Add("  • Appoint a Data Protection Officer (DPO) if required");
        recommendations.Add("  • Conduct regular GDPR compliance audits");
        recommendations.Add("  • Document all processing activities (Article 30 register)");
        recommendations.Add("  • Train staff on GDPR requirements and data protection");
        recommendations.Add("  • Review and update privacy policies annually");
        recommendations.Add("  • Implement data breach response procedures");
        recommendations.Add("  • Ensure third-party processors are GDPR compliant");

        return recommendations;
    }
}
