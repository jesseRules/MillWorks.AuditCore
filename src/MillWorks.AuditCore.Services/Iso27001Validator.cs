using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// ISO 27001 Compliance Validator
/// Validates audit logs against ISO/IEC 27001:2013 requirements
/// </summary>
public sealed class Iso27001Validator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.ISO27001;

    /// <summary>
    /// Validates audit events against ISO 27001 compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // A.12.4.1 - Event logging
        var hasEventLogging = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Event Logging (A.12.4.1)",
            Passed = hasEventLogging,
            Message = hasEventLogging
                ? $"Found {context.TotalEventCount} logged events"
                : "No events logged - event logging must be implemented",
            Severity = hasEventLogging ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "ISO 27001",
            Category = "Logging and Monitoring",
            RegulationReference = "ISO/IEC 27001:2013 A.12.4.1",
            TotalCount = context.TotalEventCount,
            FailedCount = hasEventLogging ? 0 : 1
        });

        // A.12.4.2 - Protection of log information (server-side counts)
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;

        results.Add(new AuditValidationResult
        {
            RuleName = "Log Protection (A.12.4.2)",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} events are protected with integrity controls"
                : context.TotalEventCount == 0
                    ? "No events to verify"
                    : $"{context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.High,
            ComplianceStandard = "ISO 27001",
            Category = "Log Protection",
            RegulationReference = "ISO/IEC 27001:2013 A.12.4.2",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? []
                : ["Enable tamper detection to protect log integrity"]
        });

        // A.16.1.2 - Reporting information security events
        var securityEvents = events.Where(static e =>
            e.EventType?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Incident", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Breach", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var hasSecurityEventLogging = securityEvents.Any();
        results.Add(new AuditValidationResult
        {
            RuleName = "Security Event Reporting (A.16.1.2)",
            Passed = hasSecurityEventLogging,
            Message = hasSecurityEventLogging
                ? $"Found {securityEvents.Count} security events properly logged"
                : "No security events logged - security event reporting must be implemented",
            Severity = hasSecurityEventLogging ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "ISO 27001",
            Category = "Incident Management",
            RegulationReference = "ISO/IEC 27001:2013 A.16.1.2",
            TotalCount = events.Count,
            FailedCount = hasSecurityEventLogging ? 0 : 1,
            Recommendations = hasSecurityEventLogging
                ? []
                :
                [
                    "Implement security event logging for incidents and breaches",
                    "Train staff on security event reporting procedures"
                ]
        });

        // A.9.2.1 - User registration and de-registration
        var userAccessEvents = events.Where(static e =>
            e.EventType?.Contains("User", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Login", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "User Access Management (A.9.2.1)",
            Passed = userAccessEvents.Any(),
            Message = userAccessEvents.Any()
                ? $"User access events are being logged ({userAccessEvents.Count} events)"
                : "No user access events logged",
            Severity = userAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "ISO 27001",
            Category = "Access Control",
            RegulationReference = "ISO/IEC 27001:2013 A.9.2.1",
            TotalCount = events.Count,
            FailedCount = userAccessEvents.Any() ? 0 : 1
        });

        // A.12.4.3 - Administrator and operator logs
        var adminEvents = events.Where(static e =>
            e.EventType?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.User?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Administrator Activity Logging (A.12.4.3)",
            Passed = adminEvents.Any(),
            Message = adminEvents.Any()
                ? $"Administrator activities are being logged ({adminEvents.Count} events)"
                : "No administrator activity logging detected",
            Severity = adminEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "ISO 27001",
            Category = "Administrative Controls",
            RegulationReference = "ISO/IEC 27001:2013 A.12.4.3",
            TotalCount = events.Count,
            FailedCount = adminEvents.Any() ? 0 : 1
        });

        // A.12.4.4 - Clock synchronization
        var hasTimestamps = events.All(static e => e.InsertedDate.HasValue);
        results.Add(new AuditValidationResult
        {
            RuleName = "Clock Synchronization (A.12.4.4)",
            Passed = events.Count > 0 && hasTimestamps,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate clock synchronization"
                : hasTimestamps
                    ? "All events have accurate timestamps"
                    : "Some events lack timestamps - clock synchronization required",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : hasTimestamps ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "ISO 27001",
            Category = "System Management",
            RegulationReference = "ISO/IEC 27001:2013 A.12.4.4",
            TotalCount = events.Count,
            FailedCount = events.Count(static e => !e.InsertedDate.HasValue)
        });

        // Log retention requirements
        // Use the server-side oldest event date, not the recency-biased 5,000-row sample.
        var retentionDays = context.OldestEventDate.HasValue
            ? (DateTimeOffset.UtcNow - context.OldestEventDate.Value).Days
            : 0;

        var meetsRetention = retentionDays >= 90; // Minimum 90 days recommended
        results.Add(new AuditValidationResult
        {
            RuleName = "Log Retention Requirements",
            Passed = meetsRetention,
            Message = $"Current log retention: {retentionDays} days (recommended minimum: 90 days)",
            Severity = meetsRetention ? ValidationSeverity.Info : ValidationSeverity.Low,
            ComplianceStandard = "ISO 27001",
            Category = "Log Retention",
            RegulationReference = "ISO/IEC 27001:2013 A.12.4",
            TotalCount = events.Count,
            FailedCount = meetsRetention ? 0 : 1,
            Recommendations = meetsRetention
                ? []
                :
                [
                    "Increase log retention to minimum 90 days",
                    "Configure archival system for long-term retention"
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
            recommendations.Add("ISO 27001: All validation checks passed");
            return recommendations;
        }

        // Group by severity
        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        if (critical.Any())
        {
            recommendations.Add("⚠️ CRITICAL ISSUES REQUIRING IMMEDIATE ATTENTION:");
            foreach (var result in critical)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
                recommendations.AddRange(result.Recommendations.Select(static r => $"    - {r}"));
            }
        }

        if (high.Any())
        {
            recommendations.Add("🔴 HIGH PRIORITY ISSUES:");
            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
                recommendations.AddRange(result.Recommendations.Select(static r => $"    - {r}"));
            }
        }

        if (medium.Any())
        {
            recommendations.Add("🟡 MEDIUM PRIORITY ISSUES:");
            foreach (var result in medium)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
                recommendations.AddRange(result.Recommendations.Select(static r => $"    - {r}"));
            }
        }

        if (low.Any())
        {
            recommendations.Add("🔵 LOW PRIORITY ISSUES:");
            foreach (var result in low)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
            }
        }

        return recommendations;
    }
}