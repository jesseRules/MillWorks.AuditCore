using MillWorks.AuditCore.Abstractions.Constants;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// FERPA (Family Educational Rights and Privacy Act) Compliance Validator
/// Validates audit logs against 20 U.S.C. §1232g and 34 CFR Part 99 requirements.
/// Includes both Configuration meta-rules (system setup) and Operational rules (runtime compliance).
/// </summary>
public sealed class FerpaValidator : IComplianceValidator
{
    private readonly IComplianceAttributeScanner _scanner;
    private readonly SecurityOptions? _securityOptions;

    /// <summary>
    /// Creates a new FERPA validator.
    /// </summary>
    /// <param name="scanner">Compliance attribute scanner for configuration meta-rules.</param>
    /// <param name="securityOptions">
    /// Optional security options. When provided, integrity rule severity is adjusted based on
    /// whether tamper detection is enabled. When null, integrity rule uses Info severity.
    /// </param>
    public FerpaValidator(IComplianceAttributeScanner scanner, SecurityOptions? securityOptions = null)
    {
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _securityOptions = securityOptions;
    }

    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.FERPA;

    /// <summary>
    /// Validates audit events against FERPA compliance standards.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // --- Configuration meta-rules (validate the system is set up correctly) ---

        AddEntityConfigurationRule(results);
        AddSensitiveDataProtectionRule(results);

        // --- Operational rules (validate runtime compliance) ---

        AddEducationRecordsAccessRule(results, events);
        AddUniqueUserIdentificationRule(results, events);
        AddConsentTrackingRule(results, events);
        AddDirectoryInfoOptOutRule(results, events);
        AddLegitimateEducationalInterestRule(results, events);
        AddDisclosureLoggingRule(results, events);
        AddAnnualNotificationRule(results, events);
        AddRetentionComplianceRule(results, context);
        AddIntegrityControlsRule(results, context);
        AddDeIdentificationRule(results, events);
        AddSecurityIncidentTrackingRule(results, events);
        AddLoginMonitoringRule(results, events);

        await Task.CompletedTask;
        return results;
    }

    // ── Configuration Meta-Rules ──

    private void AddEntityConfigurationRule(List<AuditValidationResult> results)
    {
        var ferpaEntities = _scanner.GetFerpaEntities();
        results.Add(new AuditValidationResult
        {
            RuleName = "FERPA Entity Configuration (§99.31)",
            Passed = ferpaEntities.Count > 0,
            Message = ferpaEntities.Count > 0
                ? $"Found {ferpaEntities.Count} entity type(s) decorated with [FERPA]: {string.Join(", ", ferpaEntities.Select(static t => t.Name))}"
                : "No entity types decorated with [FERPA] attribute found — education record entities should be marked with [FERPA] for interceptor enforcement",
            Severity = ferpaEntities.Count > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.High,
            ComplianceStandard = "FERPA",
            Category = "Configuration",
            RegulationReference = "20 U.S.C. §1232g; 34 CFR §99.31",
            TotalCount = ferpaEntities.Count,
            FailedCount = ferpaEntities.Count > 0 ? 0 : 1,
            Recommendations = ferpaEntities.Count > 0
                ? []
                :
                [
                    "Decorate entity classes containing education records with [FERPA] attribute",
                    "Example: [FERPA(RecordType = \"StudentRecord\", RequiresConsent = true)]",
                    "This enables interceptor-level consent and access logging enforcement"
                ]
        });
    }

    private void AddSensitiveDataProtectionRule(List<AuditValidationResult> results)
    {
        var sensitiveProperties = _scanner.GetSensitiveProperties(ComplianceStandard.FERPA);
        results.Add(new AuditValidationResult
        {
            RuleName = "FERPA Sensitive Data Protection (§99.31)",
            Passed = sensitiveProperties.Count > 0,
            Message = sensitiveProperties.Count > 0
                ? $"Found {sensitiveProperties.Count} [SensitiveData] properties with FERPA in ApplicableStandards across {sensitiveProperties.Select(static p => p.DeclaringType).Distinct().Count()} type(s)"
                : "No properties decorated with [SensitiveData] include FERPA in ApplicableStandards — sensitive education record fields should be tagged for encryption and masking",
            Severity = sensitiveProperties.Count > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.High,
            ComplianceStandard = "FERPA",
            Category = "Configuration",
            RegulationReference = "20 U.S.C. §1232g; 34 CFR §99.31",
            TotalCount = sensitiveProperties.Count,
            FailedCount = sensitiveProperties.Count > 0 ? 0 : 1,
            Recommendations = sensitiveProperties.Count > 0
                ? []
                :
                [
                    "Mark sensitive education record properties with [SensitiveData(ApplicableStandards = new[] { ComplianceStandard.FERPA })]",
                    "Set AutoEncrypt = true for fields requiring field-level encryption",
                    "Set MaskInLogs = true for fields that should be masked in audit log output"
                ]
        });
    }

    // ── Operational Rules ──

    /// <summary>
    /// Rule 1: Education Records Access Logging (§99.10)
    /// </summary>
    private static void AddEducationRecordsAccessRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var educationAccessEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.StudentAccess, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.EducationRecord, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Record, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Enrollment, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Grade, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Transcript, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Education Records Access Logging (§99.10)",
            Passed = educationAccessEvents > 0,
            Message = educationAccessEvents > 0
                ? $"Found {educationAccessEvents} education record access events"
                : "No education record access events found — all access to education records must be logged per FERPA §99.10",
            Severity = educationAccessEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.High,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(b); 34 CFR §99.10",
            TotalCount = events.Count,
            FailedCount = educationAccessEvents > 0 ? 0 : 1,
            Recommendations = educationAccessEvents > 0
                ?
                [
                    "Ensure all access paths (API endpoints, direct DB queries, batch jobs) are instrumented",
                    "This check verifies events are being generated — it cannot verify that all access paths are covered"
                ]
                :
                [
                    "Instrument all education record access paths to emit audit events",
                    "Include API endpoints, direct DB queries, batch jobs, and report generation",
                    "Log who accessed, what was accessed, when, and for what purpose",
                    "Include both successful and denied access attempts"
                ]
        });
    }

    /// <summary>
    /// Rule 2: Unique User Identification (§99.31(a))
    /// </summary>
    private static void AddUniqueUserIdentificationRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var hasUserIdentification = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        var eventsWithoutUser = events.Count(static e => string.IsNullOrWhiteSpace(e.User));

        results.Add(new AuditValidationResult
        {
            RuleName = "Unique User Identification (§99.31(a))",
            Passed = events.Count > 0 && hasUserIdentification,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate unique user identification"
                : hasUserIdentification
                    ? "All audit events include unique user identification"
                    : $"CRITICAL: {eventsWithoutUser} event(s) lack user identification — FERPA requires accountability for all access to education records",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : hasUserIdentification ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g; 34 CFR §99.31(a)",
            TotalCount = events.Count,
            FailedCount = eventsWithoutUser,
            Recommendations = hasUserIdentification
                ? []
                :
                [
                    "IMMEDIATE: Ensure all audit events include user identification",
                    "Configure AuditContext middleware to capture authenticated user identity",
                    "System-initiated events should use a service account identifier"
                ]
        });
    }

    /// <summary>
    /// Rule 3: Prior Written Consent Tracking (§99.30)
    /// </summary>
    private static void AddConsentTrackingRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var consentEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.Consent, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.ParentConsent, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Authorization, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Prior Written Consent Tracking (§99.30)",
            Passed = consentEvents > 0,
            Message = consentEvents > 0
                ? $"Found {consentEvents} consent/authorization events"
                : "No consent tracking events found — FERPA requires prior written consent before disclosing PII from education records (with limited exceptions)",
            Severity = consentEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Medium,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(b)(1); 34 CFR §99.30",
            TotalCount = events.Count,
            FailedCount = consentEvents > 0 ? 0 : 1,
            Recommendations = consentEvents > 0
                ? []
                :
                [
                    "Implement consent tracking for PII disclosure from education records",
                    "Log consent grants, revocations, and the scope of each consent",
                    "Consent must specify: records to be disclosed, purpose, and parties receiving records"
                ]
        });
    }

    /// <summary>
    /// Rule 4: Directory Information Opt-Out (§99.37)
    /// </summary>
    private static void AddDirectoryInfoOptOutRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var directoryOptOutEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.DirectoryOptOut, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.OptOut, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.DirectoryInfo, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Directory Information Opt-Out (§99.37)",
            Passed = directoryOptOutEvents > 0,
            Message = directoryOptOutEvents > 0
                ? $"Found {directoryOptOutEvents} directory information opt-out events"
                : "No directory information opt-out events found — institutions must allow parents/students to opt out of directory information disclosure",
            Severity = directoryOptOutEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Medium,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(a)(5)(B); 34 CFR §99.37",
            TotalCount = events.Count,
            FailedCount = directoryOptOutEvents > 0 ? 0 : 1,
            Recommendations = directoryOptOutEvents > 0
                ? []
                :
                [
                    "Implement directory information opt-out tracking",
                    "Log when parents/students request to opt out of directory information disclosure",
                    "Ensure opt-out preferences are checked before any directory information release"
                ]
        });
    }

    /// <summary>
    /// Rule 5: Legitimate Educational Interest (§99.31(a)(1))
    /// </summary>
    private static void AddLegitimateEducationalInterestRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var legitimateInterestEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.LegitimateInterest, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.AccessJustification, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.EducationalPurpose, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Legitimate Educational Interest (§99.31(a)(1))",
            Passed = legitimateInterestEvents > 0,
            Message = legitimateInterestEvents > 0
                ? $"Found {legitimateInterestEvents} legitimate educational interest justification events"
                : "No legitimate educational interest events found — access to education records without consent requires documented legitimate educational interest",
            Severity = legitimateInterestEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Medium,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(b)(1)(A); 34 CFR §99.31(a)(1)",
            TotalCount = events.Count,
            FailedCount = legitimateInterestEvents > 0 ? 0 : 1,
            Recommendations = legitimateInterestEvents > 0
                ? []
                :
                [
                    "Emit explicit access-justification events when education records are accessed without consent",
                    "Document the legitimate educational interest for each access under §99.31(a)(1)",
                    "Include the specific educational function that requires access"
                ]
        });
    }

    /// <summary>
    /// Rule 6: Disclosure Logging (§99.32)
    /// </summary>
    private static void AddDisclosureLoggingRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var disclosureEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.Disclosure, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.DataSharing, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.ThirdParty, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Disclosure Logging (§99.32)",
            Passed = disclosureEvents > 0,
            Message = disclosureEvents > 0
                ? $"Found {disclosureEvents} disclosure/data-sharing events"
                : "No disclosure events found — institutions must maintain a record of each disclosure of PII from education records",
            Severity = disclosureEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Medium,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(b)(4)(A); 34 CFR §99.32",
            TotalCount = events.Count,
            FailedCount = disclosureEvents > 0 ? 0 : 1,
            Recommendations = disclosureEvents > 0
                ? []
                :
                [
                    "Implement disclosure logging for all PII shared from education records",
                    "Record: parties receiving records, legitimate interest justification, and date of disclosure",
                    "Disclosure records must be maintained with the student's education records"
                ]
        });
    }

    /// <summary>
    /// Rule 7: Annual Notification (§99.7)
    /// </summary>
    private static void AddAnnualNotificationRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var notificationEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.FerpaNotification, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.AnnualNotice, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.RightsNotification, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Annual Notification (§99.7)",
            Passed = notificationEvents > 0,
            Message = notificationEvents > 0
                ? $"Found {notificationEvents} FERPA rights notification events"
                : "No annual FERPA notification events found — institutions must annually notify parents/eligible students of their FERPA rights",
            Severity = notificationEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Low,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "34 CFR §99.7",
            TotalCount = events.Count,
            FailedCount = notificationEvents > 0 ? 0 : 1,
            Recommendations = notificationEvents > 0
                ? []
                :
                [
                    "Implement annual FERPA rights notification tracking",
                    "Notify parents/eligible students of: right to inspect records, right to amend, consent rights, and right to file complaints",
                    "Log when notifications are sent and acknowledged"
                ]
        });
    }

    /// <summary>
    /// Rule 8: Retention Compliance (34 CFR §99.32(a)(2))
    /// Inverted logic: verifies records ARE being retained, not that old records exist.
    /// </summary>
    private static void AddRetentionComplianceRule(List<AuditValidationResult> results, ComplianceValidationContext context)
    {
        if (context.TotalEventCount == 0)
        {
            results.Add(new AuditValidationResult
            {
                RuleName = "Retention Compliance (§99.32(a)(2))",
                Passed = false,
                Message = "No audit events to evaluate retention compliance",
                Severity = ValidationSeverity.Medium,
                ComplianceStandard = "FERPA",
                Category = "Operational",
                RegulationReference = "34 CFR §99.32(a)(2)",
                TotalCount = 0,
                FailedCount = 1,
                Recommendations =
                [
                    "Ensure audit events are being generated and retained",
                    "Disclosure records must be maintained as long as the education records are maintained"
                ]
            });
            return;
        }

        // Use the server-side oldest/newest event dates, not the recency-biased 5,000-row sample.
        var oldestDate = context.OldestEventDate ?? DateTimeOffset.UtcNow;
        var newestDate = context.NewestEventDate ?? DateTimeOffset.UtcNow;
        var spanDays = (newestDate - oldestDate).Days;
        var ageDays = (DateTimeOffset.UtcNow - oldestDate).Days;

        const int fiveYearsDays = 1825;

        if (ageDays >= fiveYearsDays)
        {
            // System has 5+ years of data — meets retention
            results.Add(new AuditValidationResult
            {
                RuleName = "Retention Compliance (§99.32(a)(2))",
                Passed = true,
                Message = $"Audit logs span {ageDays} days ({ageDays / 365} years) — meets 5-year retention requirement",
                Severity = ValidationSeverity.Info,
                ComplianceStandard = "FERPA",
                Category = "Operational",
                RegulationReference = "34 CFR §99.32(a)(2)",
                TotalCount = context.TotalEventCount,
                FailedCount = 0,
                Recommendations = ["Continue maintaining long-term retention for FERPA disclosure records"]
            });
        }
        else
        {
            // System is younger than 5 years — pass with informational note
            results.Add(new AuditValidationResult
            {
                RuleName = "Retention Compliance (§99.32(a)(2))",
                Passed = true,
                Message = $"System has been operational for {ageDays} days — retention compliance will be fully verifiable after 5 years. Current data spans {spanDays} days across {context.TotalEventCount} events.",
                Severity = ValidationSeverity.Info,
                ComplianceStandard = "FERPA",
                Category = "Operational",
                RegulationReference = "34 CFR §99.32(a)(2)",
                TotalCount = context.TotalEventCount,
                FailedCount = 0,
                Recommendations =
                [
                    "Ensure retention policies are configured to retain records for at least 5 years",
                    "Disclosure records must be maintained as long as the education records are maintained",
                    "Configure archival system for long-term student record retention"
                ]
            });
        }
    }

    /// <summary>
    /// Rule 9: Integrity Controls (server-side counts)
    /// Severity depends on whether tamper detection is enabled in SecurityOptions.
    /// </summary>
    private void AddIntegrityControlsRule(List<AuditValidationResult> results, ComplianceValidationContext context)
    {
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;
        var protectedCount = context.TotalEventCount - context.UnprotectedEventCount;
        var tamperDetectionEnabled = _securityOptions?.EnableTamperDetection == true;

        // Determine severity based on configuration:
        // - Tamper detection enabled + events lack integrity = Critical
        // - Tamper detection not enabled = Info (with recommendation to enable)
        ValidationSeverity failSeverity;
        string failMessage;
        List<string> failRecommendations;

        if (tamperDetectionEnabled)
        {
            failSeverity = ValidationSeverity.Critical;
            failMessage = $"CRITICAL: Tamper detection is enabled but {context.UnprotectedEventCount} event(s) lack integrity records — education records must be protected from unauthorized alteration";
            failRecommendations =
            [
                "IMMEDIATE: Investigate why events are missing integrity records when tamper detection is enabled",
                "Verify AuditSaveChangesInterceptor is generating integrity hashes for all events",
                "Check for events created before tamper detection was enabled"
            ];
        }
        else
        {
            failSeverity = ValidationSeverity.Info;
            failMessage = "Tamper detection is not enabled — consider enabling integrity hashing to protect education records from unauthorized alteration";
            failRecommendations =
            [
                "Enable tamper detection in UseSecurity() configuration",
                "Integrity hashing provides tamper-evident audit logs for FERPA compliance",
                "Once enabled, all new audit events will include cryptographic integrity records"
            ];
        }

        results.Add(new AuditValidationResult
        {
            RuleName = "Integrity Controls",
            Passed = allProtected || (!tamperDetectionEnabled && context.TotalEventCount == 0),
            Message = allProtected
                ? $"All {context.TotalEventCount} events have integrity protection"
                : context.TotalEventCount == 0
                    ? "No events to verify"
                    : failMessage,
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : failSeverity,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? ["Ensure all audit events maintain integrity protection"]
                : failRecommendations
        });
    }

    /// <summary>
    /// Rule 10: De-identification for Research (§99.31(b))
    /// Only checked when research-related events exist.
    /// </summary>
    private static void AddDeIdentificationRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var researchEvents = events.Where(static e =>
            e.EventType?.Contains("Research", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Study", StringComparison.OrdinalIgnoreCase) == true).ToList();

        if (researchEvents.Count == 0)
        {
            // No research events — rule is not applicable, pass with info
            results.Add(new AuditValidationResult
            {
                RuleName = "De-identification for Research (§99.31(b))",
                Passed = true,
                Message = "No research-related events detected — rule not applicable",
                Severity = ValidationSeverity.Info,
                ComplianceStandard = "FERPA",
                Category = "Operational",
                RegulationReference = "20 U.S.C. §1232g(b)(1)(F); 34 CFR §99.31(b)",
                TotalCount = events.Count,
                FailedCount = 0,
                Recommendations = []
            });
            return;
        }

        var deIdentificationEvents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.DeIdentified, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Anonymized, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "De-identification for Research (§99.31(b))",
            Passed = deIdentificationEvents > 0,
            Message = deIdentificationEvents > 0
                ? $"Found {deIdentificationEvents} de-identification/anonymization events alongside {researchEvents.Count} research events"
                : $"Found {researchEvents.Count} research events but no de-identification events — FERPA requires removal of PII before disclosing education records for research",
            Severity = deIdentificationEvents > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.Medium,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g(b)(1)(F); 34 CFR §99.31(b)",
            TotalCount = researchEvents.Count,
            FailedCount = deIdentificationEvents > 0 ? 0 : researchEvents.Count,
            Recommendations = deIdentificationEvents > 0
                ? []
                :
                [
                    "Implement de-identification before disclosing education records for research",
                    "Log de-identification events when PII is removed from research datasets",
                    "Ensure re-identification risk is minimized per §99.31(b)"
                ]
        });
    }

    /// <summary>
    /// Rule 11: Security Incident Tracking
    /// </summary>
    private static void AddSecurityIncidentTrackingRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var securityIncidents = events.Count(static e =>
            e.EventType?.Contains(FerpaEventTypes.Security, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Breach, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Incident, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Violation, StringComparison.OrdinalIgnoreCase) == true);

        results.Add(new AuditValidationResult
        {
            RuleName = "Security Incident Tracking",
            Passed = true, // Having no incidents is acceptable
            Message = securityIncidents > 0
                ? $"Security incidents are being tracked ({securityIncidents} events)"
                : "No security incidents logged (ensure incident logging is configured if incidents occur)",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Document security incident response procedures for education record breaches",
                "Ensure all security incidents affecting education records are logged",
                "Notify affected students/parents of unauthorized disclosures"
            ]
        });
    }

    /// <summary>
    /// Rule 12: Login/Authentication Monitoring
    /// </summary>
    private static void AddLoginMonitoringRule(List<AuditValidationResult> results, List<AuditEventEntity> events)
    {
        var loginEvents = events.Where(static e =>
            e.EventType?.Contains(FerpaEventTypes.Login, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.Authentication, StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains(FerpaEventTypes.SignIn, StringComparison.OrdinalIgnoreCase) == true).ToList();

        var failedLogins = loginEvents.Where(static e =>
            e.EventType?.Contains("Failed", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Denied", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Login/Authentication Monitoring",
            Passed = loginEvents.Count > 0,
            Message = loginEvents.Count > 0
                ? $"Login attempts are being monitored ({loginEvents.Count} login events, {failedLogins.Count} failed)"
                : "No login/authentication events detected — monitoring login attempts is essential for protecting access to education records",
            Severity = loginEvents.Count > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.High,
            ComplianceStandard = "FERPA",
            Category = "Operational",
            RegulationReference = "20 U.S.C. §1232g",
            TotalCount = events.Count,
            FailedCount = loginEvents.Count > 0 ? 0 : 1,
            Recommendations = loginEvents.Count > 0
                ?
                [
                    "Continue monitoring for suspicious login patterns",
                    "Investigate multiple failed login attempts targeting education record systems"
                ]
                :
                [
                    "Implement comprehensive login monitoring for education record systems",
                    "Log both successful and failed authentication attempts",
                    "Include IP address, timestamp, and user identification"
                ]
        });
    }

    /// <summary>
    /// Generates recommendations based on the validation results.
    /// Groups by severity tier following the HipaaValidator pattern.
    /// </summary>
    public List<string> GenerateRecommendations(IEnumerable<AuditValidationResult> validationResults)
    {
        var recommendations = new List<string>();
        var failedResults = validationResults.Where(static r => !r.Passed)
            .OrderByDescending(static r => r.Severity)
            .ToList();

        if (failedResults.Count == 0)
        {
            recommendations.Add("FERPA COMPLIANCE: All validation checks passed");
            recommendations.Add("");
            recommendations.Add("Ongoing Compliance Requirements:");
            recommendations.Add("  - Review access logs regularly for unauthorized education record access");
            recommendations.Add("  - Maintain disclosure records as long as the education records are maintained");
            recommendations.Add("  - Send annual FERPA rights notification to parents/eligible students");
            recommendations.Add("  - Review and update directory information designations annually");
            recommendations.Add("  - Ensure all new data access paths are instrumented for audit logging");
            recommendations.Add("  - Conduct periodic review of legitimate educational interest policies");
            return recommendations;
        }

        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        recommendations.Add("FERPA COMPLIANCE REPORT");
        recommendations.Add($"Found {failedResults.Count} compliance issue(s) requiring attention");
        recommendations.Add("");

        if (critical.Count > 0)
        {
            recommendations.Add("CRITICAL - IMMEDIATE ACTION REQUIRED:");
            recommendations.Add("Failure to address these issues may result in loss of federal funding.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  {result.RuleName}");
                recommendations.Add($"     Regulation: {result.RegulationReference}");
                recommendations.Add($"     Issue: {result.Message}");
                if (result.FailedCount > 0)
                    recommendations.Add($"     Impact: {result.FailedCount} of {result.TotalCount} events affected");

                if (result.Recommendations.Count > 0)
                {
                    recommendations.Add("     REQUIRED ACTIONS:");
                    foreach (var rec in result.Recommendations)
                        recommendations.Add($"     - {rec}");
                }

                recommendations.Add("");
            }
        }

        if (high.Count > 0)
        {
            recommendations.Add("HIGH PRIORITY - Address Within 30 Days:");
            recommendations.Add("These issues represent significant compliance gaps.");
            recommendations.Add("");

            foreach (var result in high)
            {
                recommendations.Add($"  - {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    Issue: {result.Message}");
                if (result.Recommendations.Count > 0)
                {
                    recommendations.Add("    Actions:");
                    foreach (var rec in result.Recommendations)
                        recommendations.Add($"    - {rec}");
                }

                recommendations.Add("");
            }
        }

        if (medium.Count > 0)
        {
            recommendations.Add("MEDIUM PRIORITY - Address Within 90 Days:");
            recommendations.Add("These are important compliance requirements that should be implemented.");
            recommendations.Add("");

            foreach (var result in medium)
            {
                recommendations.Add($"  - {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    {result.Message}");
                if (result.Recommendations.Count > 0)
                {
                    foreach (var rec in result.Recommendations)
                        recommendations.Add($"    - {rec}");
                }
            }

            recommendations.Add("");
        }

        if (low.Count > 0)
        {
            recommendations.Add("LOW PRIORITY - Best Practices:");
            foreach (var result in low)
            {
                recommendations.Add($"  - {result.RuleName}: {result.Message}");
            }

            recommendations.Add("");
        }

        // FERPA-specific resources
        recommendations.Add("FERPA Compliance Resources:");
        recommendations.Add("  - U.S. Department of Education FERPA page: https://studentprivacy.ed.gov/");
        recommendations.Add("  - Privacy Technical Assistance Center (PTAC): https://studentprivacy.ed.gov/resources");
        recommendations.Add("  - FERPA Regulations (34 CFR Part 99): https://www.ecfr.gov/current/title-34/subtitle-A/part-99");
        recommendations.Add("");

        // Penalty information
        recommendations.Add("Enforcement Information:");
        recommendations.Add("  - FERPA violations may result in termination of federal funding under 20 U.S.C. §1232g(b)");
        recommendations.Add("  - Complaints may be filed with the Family Policy Compliance Office (FPCO)");
        recommendations.Add("  - Institutions must comply within a reasonable period or face funding termination");
        recommendations.Add("");

        recommendations.Add("FERPA applies to all educational agencies and institutions receiving federal funds.");
        recommendations.Add("Audit logging is one component — policies, training, and access controls are equally required.");

        return recommendations;
    }
}
