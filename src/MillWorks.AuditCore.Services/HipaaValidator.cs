using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// HIPAA (Health Insurance Portability and Accountability Act) Compliance Validator
/// Validates audit logs against HIPAA Security Rule (45 CFR Part 164) requirements
/// </summary>
public sealed class HipaaValidator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.HIPAA;

    /// <summary>
    /// Validates audit events against HIPAA compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // §164.312(b) - Audit Controls (REQUIRED)
        var hasAuditControls = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Controls (§164.312(b))",
            Passed = hasAuditControls,
            Message = hasAuditControls
                ? $"Audit controls are implemented ({events.Count} audit events recorded)"
                : "CRITICAL: No audit controls detected - HIPAA requires hardware, software, and/or procedural mechanisms to record and examine activity",
            Severity = hasAuditControls ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Audit Controls",
            RegulationReference = "45 CFR §164.312(b)",
            TotalCount = events.Count,
            FailedCount = hasAuditControls ? 0 : 1,
            Recommendations = hasAuditControls
                ? []
                :
                [
                    "IMMEDIATE: Implement comprehensive audit logging for all ePHI access and systems",
                    "Hardware, software, and procedural mechanisms must record system activity",
                    "This is a REQUIRED implementation specification under HIPAA"
                ]
        });

        // §164.308(a)(1)(ii)(D) - Information System Activity Review
        var recentEvents = events.Where(static e => e.InsertedDate >= DateTimeOffset.UtcNow.AddDays(-30)).ToList();
        var hasRecentActivity = recentEvents.Any();

        results.Add(new AuditValidationResult
        {
            RuleName = "Information System Activity Review (§164.308(a)(1)(ii)(D))",
            Passed = hasRecentActivity,
            Message = hasRecentActivity
                ? $"System activity is being reviewed ({recentEvents.Count} events in last 30 days)"
                : "No recent audit events - regular review of system activity is required",
            Severity = hasRecentActivity ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "HIPAA",
            Category = "Administrative Safeguards - Security Management",
            RegulationReference = "45 CFR §164.308(a)(1)(ii)(D)",
            TotalCount = events.Count,
            FailedCount = hasRecentActivity ? 0 : events.Count,
            Recommendations = hasRecentActivity
                ? ["Continue regular review of audit logs, reports, and system activity"]
                :
                [
                    "Implement regular review procedures for audit logs",
                    "Assign responsibility for reviewing system activity",
                    "Document review procedures and findings"
                ]
        });

        // §164.312(a)(1) - Access Control - PHI Access Logging
        var phiAccessEvents = events.Where(static e =>
            e.EventType?.Contains("PHI", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Patient", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Medical", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Health", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Record", StringComparison.OrdinalIgnoreCase) == true ||
            e.EntityType?.Contains("Patient", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "PHI Access Logging (§164.312(a)(1))",
            Passed = phiAccessEvents.Any(),
            Message = phiAccessEvents.Any()
                ? $"Protected Health Information (PHI/ePHI) access is being logged ({phiAccessEvents.Count} events)"
                : "CRITICAL: No PHI access events detected - all ePHI access must be logged with user identification",
            Severity = phiAccessEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Access Control",
            RegulationReference = "45 CFR §164.312(a)(1)",
            TotalCount = events.Count,
            FailedCount = phiAccessEvents.Any() ? 0 : 1,
            Recommendations = phiAccessEvents.Any()
                ?
                [
                    "Ensure ALL ePHI access (view, create, modify, delete) is logged",
                    "Include minimum necessary rule compliance in access logging"
                ]
                :
                [
                    "IMMEDIATE: Implement comprehensive ePHI access logging",
                    "Log who accessed, what was accessed, when, and from where",
                    "Include both successful and failed access attempts",
                    "This is critical for breach investigations and compliance audits"
                ]
        });

        // §164.312(a)(2)(i) - Unique User Identification (REQUIRED)
        var hasUserIdentification = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        var eventsWithoutUser = events.Count(static e => string.IsNullOrWhiteSpace(e.User));

        results.Add(new AuditValidationResult
        {
            RuleName = "Unique User Identification (§164.312(a)(2)(i))",
            Passed = events.Count > 0 && hasUserIdentification,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate unique user identification"
                : hasUserIdentification
                    ? "All audit events include unique user identification"
                    : $"CRITICAL: {eventsWithoutUser} events lack user identification - HIPAA requires unique user IDs for accountability",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : hasUserIdentification ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Access Control",
            RegulationReference = "45 CFR §164.312(a)(2)(i)",
            TotalCount = events.Count,
            FailedCount = eventsWithoutUser,
            Recommendations = hasUserIdentification
                ? []
                :
                [
                    "IMMEDIATE: Assign unique user IDs to all system users",
                    "Ensure audit logs capture user identification for all activities",
                    "This is a REQUIRED implementation specification"
                ]
        });

        // §164.312(a)(2)(iii) - Automatic Logoff (ADDRESSABLE)
        var logoffEvents = events.Where(static e =>
            e.EventType?.Contains("Logoff", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Logout", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SessionEnd", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Timeout", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Automatic Logoff Tracking (§164.312(a)(2)(iii))",
            Passed = logoffEvents.Any(),
            Message = logoffEvents.Any()
                ? $"Automatic logoff/session timeout is being logged ({logoffEvents.Count} events)"
                : "Consider implementing and logging automatic logoff for inactive sessions",
            Severity = logoffEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Access Control",
            RegulationReference = "45 CFR §164.312(a)(2)(iii) (Addressable)",
            TotalCount = events.Count,
            FailedCount = 0, // Addressable, not required
            Recommendations = logoffEvents.Any()
                ? []
                :
                [
                    "Implement automatic logoff after period of inactivity",
                    "Log session timeouts and user logoffs",
                    "Document if not implementing (addressable specification)"
                ]
        });

        // §164.312(c)(1) - Integrity Controls (server-side counts)
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;
        var protectedCount = context.TotalEventCount - context.UnprotectedEventCount;

        results.Add(new AuditValidationResult
        {
            RuleName = "Integrity Controls (§164.312(c)(1))",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} audit events have integrity protection"
                : context.TotalEventCount == 0
                    ? "No events to verify - insufficient data"
                    : protectedCount > 0
                        ? $"{context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection"
                        : "CRITICAL: Audit logs lack integrity protection - must be tamper-evident",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.Critical,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Integrity",
            RegulationReference = "45 CFR §164.312(c)(1)",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? ["Ensure all audit logs maintain integrity protection"]
                :
                [
                    "IMMEDIATE: Implement tamper detection mechanisms",
                    "Use cryptographic hashing or digital signatures",
                    "Prevent unauthorized modification or destruction of ePHI and audit logs",
                    "This is a REQUIRED implementation specification"
                ]
        });

        // §164.312(c)(2) - Mechanism to Authenticate ePHI (ADDRESSABLE)
        results.Add(new AuditValidationResult
        {
            RuleName = "Authentication Mechanism (§164.312(c)(2))",
            Passed = allProtected,
            Message = allProtected
                ? "Electronic signatures/authentication mechanisms are in place"
                : "Consider implementing mechanisms to authenticate ePHI has not been altered",
            Severity = allProtected ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Integrity",
            RegulationReference = "45 CFR §164.312(c)(2) (Addressable)",
            TotalCount = context.TotalEventCount,
            FailedCount = 0,
            Recommendations = allProtected
                ? []
                :
                [
                    "Implement digital signatures for critical ePHI",
                    "Use checksums or hash functions to detect alterations",
                    "Document if not implementing (addressable specification)"
                ]
        });

        // §164.308(a)(5)(ii)(C) - Log-in Monitoring
        var loginEvents = events.Where(static e =>
            e.EventType?.Contains("Login", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Authentication", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SignIn", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var failedLogins = loginEvents.Where(static e =>
            e.EventType?.Contains("Failed", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Denied", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Log-in Monitoring (§164.308(a)(5)(ii)(C))",
            Passed = loginEvents.Any(),
            Message = loginEvents.Any()
                ? $"Login attempts are being monitored ({loginEvents.Count} login events, {failedLogins.Count} failed)"
                : "Login monitoring not detected - should log both successful and failed login attempts",
            Severity = loginEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "HIPAA",
            Category = "Administrative Safeguards - Security Awareness",
            RegulationReference = "45 CFR §164.308(a)(5)(ii)(C) (Addressable)",
            TotalCount = events.Count,
            FailedCount = loginEvents.Any() ? 0 : 1,
            Recommendations = loginEvents.Any()
                ?
                [
                    "Continue monitoring for suspicious login patterns",
                    "Investigate multiple failed login attempts",
                    "Alert on unusual access times or locations"
                ]
                :
                [
                    "Implement comprehensive login monitoring",
                    "Log both successful and failed authentication attempts",
                    "Include IP address, timestamp, and user identification"
                ]
        });

        // §164.308(a)(6)(ii) - Security Incident Procedures
        var securityIncidents = events.Where(static e =>
            e.EventType?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Incident", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Breach", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Violation", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Security Incident Tracking (§164.308(a)(6)(ii))",
            Passed = true, // Having none is acceptable
            Message = securityIncidents.Any()
                ? $"Security incidents are being tracked and documented ({securityIncidents.Count} events)"
                : "No security incidents logged (ensure incident logging is configured if incidents occur)",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "HIPAA",
            Category = "Administrative Safeguards - Security Incident Procedures",
            RegulationReference = "45 CFR §164.308(a)(6)(ii)",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Document security incident response procedures",
                "Ensure all security incidents are logged and tracked",
                "Maintain incident response documentation per HIPAA Breach Notification Rule",
                "Report breaches of unsecured PHI affecting 500+ individuals to HHS within 60 days"
            ]
        });

        // §164.316(b)(1)(i) - Time Limit (Addressable) - Retention Requirements
        // Use the server-side oldest event date, not the recency-biased 5,000-row sample.
        var retentionDays = context.OldestEventDate.HasValue
            ? (DateTimeOffset.UtcNow - context.OldestEventDate.Value).Days
            : 0;

        // HIPAA requires 6 years for documentation, many entities apply this to audit logs
        var meetsRetention = retentionDays >= 2190; // 6 years
        var hasRetentionPolicy = retentionDays > 0;

        results.Add(new AuditValidationResult
        {
            RuleName = "Documentation Retention (§164.316(b)(1)(i))",
            Passed = hasRetentionPolicy,
            Message = meetsRetention
                ? $"Audit logs meet 6-year retention requirement (current: {retentionDays} days)"
                : hasRetentionPolicy
                    ? $"Audit log retention: {retentionDays} days (HIPAA recommends 6 years / 2190 days for compliance documentation)"
                    : "No retention policy detected",
            Severity = meetsRetention ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "HIPAA",
            Category = "Administrative Safeguards - Documentation",
            RegulationReference = "45 CFR §164.316(b)(1)(i) (Addressable)",
            TotalCount = events.Count,
            FailedCount = meetsRetention ? 0 : 1,
            Recommendations = meetsRetention
                ? ["Continue maintaining 6-year retention for compliance documentation"]
                :
                [
                    "Implement 6-year retention policy for HIPAA documentation",
                    "Configure automatic archival for long-term retention",
                    "Document retention policies and procedures",
                    "If not implementing 6-year retention, document rationale (addressable)"
                ]
        });

        // §164.308(a)(3)(ii)(A) - Authorization and/or Supervision
        var accessChangeEvents = events.Where(static e =>
            e.EventType?.Contains("Authorization", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Permission", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Role", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Authorization Tracking (§164.308(a)(3)(ii)(A))",
            Passed = accessChangeEvents.Any(),
            Message = accessChangeEvents.Any()
                ? $"Authorization changes are being tracked ({accessChangeEvents.Count} events)"
                : "Consider logging authorization and access control changes",
            Severity = accessChangeEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "HIPAA",
            Category = "Administrative Safeguards - Workforce Security",
            RegulationReference = "45 CFR §164.308(a)(3)(ii)(A) (Addressable)",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations = accessChangeEvents.Any()
                ? []
                :
                [
                    "Log workforce member authorization changes",
                    "Track access level modifications",
                    "Document supervisory oversight of access"
                ]
        });

        // Emergency Access Procedures
        var emergencyAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Emergency", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Override", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("BreakGlass", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Emergency Access Procedures (§164.312(a)(2)(ii))",
            Passed = true,
            Message = emergencyAccessEvents.Any()
                ? $"Emergency access procedures are documented in audit logs ({emergencyAccessEvents.Count} events)"
                : "No emergency access events (ensure emergency access procedures are documented if used)",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "HIPAA",
            Category = "Technical Safeguards - Access Control",
            RegulationReference = "45 CFR §164.312(a)(2)(ii) (Required)",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Document emergency access procedures",
                "Log all emergency/break-glass access",
                "Review emergency access usage regularly",
                "Ensure minimum necessary standard applies even in emergencies"
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
            recommendations.Add("✅ HIPAA COMPLIANCE: All validation checks passed");
            recommendations.Add("");
            recommendations.Add("📋 Ongoing Compliance Requirements:");
            recommendations.Add("  • Conduct annual risk assessments");
            recommendations.Add("  • Review and update policies annually");
            recommendations.Add("  • Maintain Business Associate Agreements (BAAs)");
            recommendations.Add("  • Conduct regular workforce training");
            recommendations.Add("  • Review audit logs monthly for suspicious activity");
            recommendations.Add("  • Test breach notification procedures");
            return recommendations;
        }

        // Group by severity
        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        recommendations.Add("⚕️ HIPAA COMPLIANCE REPORT");
        recommendations.Add($"Found {failedResults.Count} compliance issue(s) requiring attention");
        recommendations.Add("");

        if (critical.Any())
        {
            recommendations.Add("🚨 CRITICAL - REQUIRED SPECIFICATIONS - IMMEDIATE ACTION REQUIRED:");
            recommendations.Add("These are REQUIRED implementation specifications under HIPAA Security Rule.");
            recommendations.Add("Failure to implement may result in significant penalties.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  ⚠️ {result.RuleName}");
                recommendations.Add($"     Regulation: {result.RegulationReference}");
                recommendations.Add($"     Issue: {result.Message}");
                if (result.FailedCount > 0)
                {
                    recommendations.Add($"     Impact: {result.FailedCount} of {result.TotalCount} events affected");
                }

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
            recommendations.Add("🔴 HIGH PRIORITY - Address Within 30 Days:");
            recommendations.Add("These issues represent significant compliance gaps.");
            recommendations.Add("");

            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    Issue: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("    Actions:");
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
            recommendations.Add("🟡 MEDIUM PRIORITY - ADDRESSABLE SPECIFICATIONS:");
            recommendations.Add("These are addressable specifications. Implement or document why not applicable.");
            recommendations.Add("");

            foreach (var result in medium)
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
            }

            recommendations.Add("");
        }

        if (low.Any())
        {
            recommendations.Add("🔵 LOW PRIORITY - Best Practices:");
            foreach (var result in low)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
            }

            recommendations.Add("");
        }

        // Add HIPAA-specific guidance
        recommendations.Add("📚 HIPAA Compliance Resources:");
        recommendations.Add(
            "  • Security Risk Assessment Tool: https://www.healthit.gov/topic/privacy-security-and-hipaa/security-risk-assessment-tool");
        recommendations.Add("  • HHS HIPAA for Professionals: https://www.hhs.gov/hipaa/for-professionals/index.html");
        recommendations.Add(
            "  • OCR Audit Protocol: https://www.hhs.gov/hipaa/for-professionals/compliance-enforcement/audit/protocol/index.html");
        recommendations.Add("");

        recommendations.Add("⚖️ Penalty Information:");
        recommendations.Add("  • Willful neglect (not corrected): $50,000 per violation, up to $1.5M per year");
        recommendations.Add("  • Willful neglect (corrected within 30 days): $10,000-$50,000 per violation");
        recommendations.Add("  • Reasonable cause: $1,000-$50,000 per violation");
        recommendations.Add("");

        recommendations.Add("🔒 Remember: HIPAA requires BOTH technical and administrative safeguards.");
        recommendations.Add("Audit logging alone is not sufficient for compliance.");

        return recommendations;
    }
}