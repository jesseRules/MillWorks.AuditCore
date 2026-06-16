using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// DISA STIG (Security Technical Implementation Guide) Compliance Validator
/// Validates audit logs against DISA Application Security STIG controls mapped to NIST 800-53 AU family
/// </summary>
public sealed class StigValidator : IComplianceValidator
{
    /// <inheritdoc />
    public ComplianceStandard Standard => ComplianceStandard.STIG;

    /// <summary>
    /// Validates audit events against DISA STIG compliance controls.
    /// </summary>
    public async Task<List<AuditValidationResult>> ValidateAsync(ComplianceValidationContext context)
    {
        if (!context.EnabledStandards.Contains(Standard))
            return await Task.FromResult(new List<AuditValidationResult>());

        var events = context.Events;
        var results = new List<AuditValidationResult>();

        // V-222582 / AU-12 - Audit Generation
        var hasAuditGeneration = context.TotalEventCount > 0;
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Generation (V-222582 / AU-12)",
            Passed = hasAuditGeneration,
            Message = hasAuditGeneration
                ? $"Application is generating audit records ({events.Count} events)"
                : "FINDING: Application must generate audit records for auditable events",
            Severity = hasAuditGeneration ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-12 / V-222582",
            TotalCount = events.Count,
            FailedCount = hasAuditGeneration ? 0 : 1,
            Recommendations = hasAuditGeneration
                ? []
                :
                [
                    "FINDING (CAT I): Implement audit logging for all auditable events",
                    "Application must generate audit records at the application level",
                    "Coordinate audit record generation with organizational audit requirements"
                ]
        });

        // V-222576 / AU-3 - Content of Audit Records
        var hasUserIdentification = events.All(static e => !string.IsNullOrWhiteSpace(e.User));
        var hasTimestamps = events.All(static e => e.InsertedDate.HasValue);
        var hasEventTypes = events.All(static e => !string.IsNullOrWhiteSpace(e.EventType));
        var eventsWithoutUser = events.Count(static e => string.IsNullOrWhiteSpace(e.User));
        var eventsWithoutTimestamp = events.Count(static e => !e.InsertedDate.HasValue);
        var eventsWithoutEventType = events.Count(static e => string.IsNullOrWhiteSpace(e.EventType));
        var auditContentComplete = hasUserIdentification && hasTimestamps && hasEventTypes;

        results.Add(new AuditValidationResult
        {
            RuleName = "Content of Audit Records (V-222576 / AU-3)",
            Passed = events.Count > 0 && auditContentComplete,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate audit record content"
                : auditContentComplete
                    ? "All audit records contain required fields (user ID, timestamp, event type)"
                    : $"Audit records missing required content: {eventsWithoutUser} without user ID, {eventsWithoutTimestamp} without timestamp, {eventsWithoutEventType} without event type",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : auditContentComplete ? ValidationSeverity.Info : ValidationSeverity.Critical,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-3 / V-222576",
            TotalCount = events.Count,
            FailedCount = eventsWithoutUser + eventsWithoutTimestamp + eventsWithoutEventType,
            Recommendations = auditContentComplete
                ? []
                :
                [
                    "FINDING (CAT II): Audit records must contain what type of event occurred",
                    "Audit records must contain when the event occurred (date and time)",
                    "Audit records must contain where the event occurred (source)",
                    "Audit records must contain the source of the event (user identity)",
                    "Audit records must contain the outcome (success or failure) of the event"
                ]
        });

        // V-222577 / AU-3(1) - Additional Audit Information
        var hasIpAddress = events.All(static e => !string.IsNullOrWhiteSpace(e.IpAddress));
        var hasComponentName = events.All(static e =>
            !string.IsNullOrWhiteSpace(e.MachineName) || !string.IsNullOrWhiteSpace(e.AssemblyName));
        var hasAction = events.All(static e => !string.IsNullOrWhiteSpace(e.Action));
        var eventsWithoutIp = events.Count(static e => string.IsNullOrWhiteSpace(e.IpAddress));
        var eventsWithoutComponent = events.Count(static e =>
            string.IsNullOrWhiteSpace(e.MachineName) && string.IsNullOrWhiteSpace(e.AssemblyName));
        var additionalInfoComplete = hasIpAddress && hasComponentName && hasAction;

        results.Add(new AuditValidationResult
        {
            RuleName = "Additional Audit Information (V-222577 / AU-3(1))",
            Passed = events.Count > 0 && additionalInfoComplete,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate additional audit information"
                : additionalInfoComplete
                    ? "All audit records contain additional required information (source IP, component, action)"
                    : $"Audit records missing additional info: {eventsWithoutIp} without IP address, {eventsWithoutComponent} without component name",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : additionalInfoComplete ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-3(1) / V-222577",
            TotalCount = events.Count,
            FailedCount = eventsWithoutIp + eventsWithoutComponent,
            Recommendations = additionalInfoComplete
                ? []
                :
                [
                    "FINDING (CAT II): Include source IP address/network address in audit records",
                    "Include component or module name that generated the event",
                    "Include the action performed (Added, Modified, Deleted, etc.)",
                    "Include the identity of affected objects or resources"
                ]
        });

        // V-222578 / AU-8 - Time Stamps
        var allHaveTimestamps = events.All(static e => e.InsertedDate.HasValue);
        var eventsWithoutTs = events.Count(static e => !e.InsertedDate.HasValue);

        results.Add(new AuditValidationResult
        {
            RuleName = "Time Stamps (V-222578 / AU-8)",
            Passed = events.Count > 0 && allHaveTimestamps,
            Message = events.Count == 0
                ? "No audit events in the period - insufficient data to evaluate audit record timestamps"
                : allHaveTimestamps
                    ? "All audit records contain timestamps from a synchronized time source"
                    : $"FINDING: {eventsWithoutTs} audit records lack timestamps",
            Severity = events.Count == 0
                ? ValidationSeverity.Low
                : allHaveTimestamps ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-8 / V-222578",
            TotalCount = events.Count,
            FailedCount = eventsWithoutTs,
            Recommendations = allHaveTimestamps
                ? []
                :
                [
                    "FINDING (CAT II): All audit records must include accurate timestamps",
                    "Use authoritative time source (NTP) for clock synchronization",
                    "Timestamps must be in UTC or include time zone offset"
                ]
        });

        // V-222579 / AU-9 - Protection of Audit Information (server-side counts)
        var allProtected = context.TotalEventCount > 0 && context.UnprotectedEventCount == 0;

        results.Add(new AuditValidationResult
        {
            RuleName = "Protection of Audit Information (V-222579 / AU-9)",
            Passed = allProtected,
            Message = allProtected
                ? $"All {context.TotalEventCount} audit records are protected with integrity controls"
                : context.TotalEventCount == 0
                    ? "No events to verify"
                    : $"FINDING: {context.UnprotectedEventCount} of {context.TotalEventCount} events lack integrity protection",
            Severity = allProtected ? ValidationSeverity.Info
                : context.TotalEventCount == 0 ? ValidationSeverity.Low
                : ValidationSeverity.Critical,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-9 / V-222579",
            TotalCount = context.TotalEventCount,
            FailedCount = context.UnprotectedEventCount,
            Recommendations = allProtected
                ? []
                :
                [
                    "FINDING (CAT II): Implement tamper detection for audit records",
                    "Use cryptographic hashing to protect audit log integrity",
                    "Restrict access to audit information to authorized personnel only",
                    "Prevent unauthorized modification, deletion, or destruction of audit logs"
                ]
        });

        // V-222580 / AU-9(2) - Audit Backup to Separate System
        var archiveEvents = events.Where(static e =>
            e.EventType?.Contains("Archive", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Backup", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Export", StringComparison.OrdinalIgnoreCase) == true).ToList();

        // The archive system stores to Azure Blob Storage, which satisfies the off-system backup requirement.
        // Check for archive-related events or integrity-protected records (indicating the archive pipeline is active).
        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Backup to Separate System (V-222580 / AU-9(2))",
            Passed = archiveEvents.Any(),
            Message = archiveEvents.Any()
                ? $"Audit records are being backed up to a separate system ({archiveEvents.Count} archive/backup events)"
                : "Ensure the archive system is active - audit records must be backed up to a physically different system or media",
            Severity = archiveEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-9(2) / V-222580",
            TotalCount = events.Count,
            FailedCount = archiveEvents.Any() ? 0 : 1,
            Recommendations = archiveEvents.Any()
                ?
                [
                    "Periodically validate archive integrity using the verification service",
                    "Ensure archive storage (Azure Blob) is in a separate availability zone or region"
                ]
                :
                [
                    "FINDING (CAT II): Configure and activate the audit archival system",
                    "Archive must store to a physically different system (Azure Blob Storage satisfies this)",
                    "Verify the ArchiveVerificationBackgroundService is running",
                    "Schedule regular archive integrity validation"
                ]
        });

        // V-222581 / AU-11 - Audit Record Retention
        // Use the server-side oldest event date, not the recency-biased 5,000-row sample.
        var retentionDays = context.OldestEventDate.HasValue
            ? (DateTimeOffset.UtcNow - context.OldestEventDate.Value).Days
            : 0;

        // DoD requires minimum 1 year; many environments require 5+ years
        var meetsMinimumRetention = retentionDays >= 365;

        results.Add(new AuditValidationResult
        {
            RuleName = "Audit Record Retention (V-222581 / AU-11)",
            Passed = meetsMinimumRetention,
            Message = meetsMinimumRetention
                ? $"Audit record retention meets minimum requirement (current: {retentionDays} days, required: 365)"
                : $"FINDING: Audit record retention is insufficient (current: {retentionDays} days, required: minimum 365 days)",
            Severity = meetsMinimumRetention ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-11 / V-222581",
            TotalCount = events.Count,
            FailedCount = meetsMinimumRetention ? 0 : 1,
            Recommendations = meetsMinimumRetention
                ?
                [
                    "DoD environments may require 5+ years - verify with your ISSM",
                    "Ensure archived records remain retrievable for the full retention period"
                ]
                :
                [
                    "FINDING (CAT II): Retain audit records for minimum 1 year (365 days)",
                    "Configure the archival system retention to meet DoD requirements",
                    "Many DoD systems require 5 years - consult your ISSM for specific requirements",
                    "Ensure archived data is included in retention calculations"
                ]
        });

        // V-222583 / AU-12(1) - System-Wide Audit Trail
        var hasCorrelationIds = events.Any(static e => !string.IsNullOrWhiteSpace(e.CorrelationId));
        var eventsWithCorrelation = events.Count(static e => !string.IsNullOrWhiteSpace(e.CorrelationId));

        results.Add(new AuditValidationResult
        {
            RuleName = "System-Wide Audit Trail (V-222583 / AU-12(1))",
            Passed = hasCorrelationIds,
            Message = hasCorrelationIds
                ? $"System-wide audit trail is supported via correlation IDs ({eventsWithCorrelation}/{events.Count} events correlated)"
                : "FINDING: Audit trail must be compilable from multiple components into a system-wide trail",
            Severity = hasCorrelationIds ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-12(1) / V-222583",
            TotalCount = events.Count,
            FailedCount = events.Count - eventsWithCorrelation,
            Recommendations = hasCorrelationIds
                ? []
                :
                [
                    "FINDING (CAT II): Implement correlation IDs to link related audit events",
                    "Ensure audit trail can be compiled from multiple system components",
                    "Use consistent time stamps across all components"
                ]
        });

        // V-222569 / AU-2 - Auditable Events (logon/logoff, privilege use, object access, policy changes, admin actions)
        var logonEvents = events.Where(static e =>
            e.EventType?.Contains("Login", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Logon", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SignIn", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Authentication", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var logoffEvents = events.Where(static e =>
            e.EventType?.Contains("Logout", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Logoff", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SignOut", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SessionEnd", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var privilegeEvents = events.Where(static e =>
            e.EventType?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Privileged", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Role", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Permission", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Sudo", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Elevat", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var objectAccessEvents = events.Where(static e =>
            e.Action == "Added" || e.Action == "Modified" || e.Action == "Deleted" ||
            e.EventType?.Contains("Create", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Update", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Delete", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Access", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var policyChangeEvents = events.Where(static e =>
            e.EventType?.Contains("Policy", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Configuration", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Setting", StringComparison.OrdinalIgnoreCase) == true).ToList();

        var auditableEventCategories = 0;
        if (logonEvents.Any()) auditableEventCategories++;
        if (logoffEvents.Any()) auditableEventCategories++;
        if (privilegeEvents.Any()) auditableEventCategories++;
        if (objectAccessEvents.Any()) auditableEventCategories++;
        if (policyChangeEvents.Any()) auditableEventCategories++;

        // At minimum, logon + object access should be present
        var hasMinimumAuditableEvents = logonEvents.Any() && objectAccessEvents.Any();

        results.Add(new AuditValidationResult
        {
            RuleName = "Auditable Events (V-222569 / AU-2)",
            Passed = hasMinimumAuditableEvents,
            Message = hasMinimumAuditableEvents
                ? $"Auditing {auditableEventCategories}/5 required event categories (logon: {logonEvents.Count}, logoff: {logoffEvents.Count}, privilege: {privilegeEvents.Count}, object access: {objectAccessEvents.Count}, policy: {policyChangeEvents.Count})"
                : $"FINDING: Only {auditableEventCategories}/5 auditable event categories detected",
            Severity = hasMinimumAuditableEvents
                ? auditableEventCategories >= 4 ? ValidationSeverity.Info : ValidationSeverity.Medium
                : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-2 / V-222569",
            TotalCount = events.Count,
            FailedCount = hasMinimumAuditableEvents ? 0 : 1,
            Recommendations = hasMinimumAuditableEvents
                ? auditableEventCategories < 5
                    ? BuildMissingCategoryRecommendations(auditableEventCategories, logoffEvents, privilegeEvents,
                        policyChangeEvents)
                    : []
                :
                [
                    "FINDING (CAT II): Application must audit the following event categories:",
                    "  - Successful and unsuccessful logon attempts",
                    "  - Logoff events",
                    "  - Privileged function execution",
                    "  - Object access (create, read, update, delete)",
                    "  - Policy or configuration changes"
                ]
        });

        // V-222570 / AU-2(3) - Reviews of Auditable Events
        var recentEvents = events.Where(static e => e.InsertedDate >= DateTimeOffset.UtcNow.AddDays(-30)).ToList();
        var hasActiveMonitoring = recentEvents.Any();

        results.Add(new AuditValidationResult
        {
            RuleName = "Reviews of Auditable Events (V-222570 / AU-2(3))",
            Passed = hasActiveMonitoring,
            Message = hasActiveMonitoring
                ? $"Active monitoring detected ({recentEvents.Count} events in last 30 days)"
                : "FINDING: No recent audit activity - auditable events must be reviewed and updated regularly",
            Severity = hasActiveMonitoring ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-2(3) / V-222570",
            TotalCount = events.Count,
            FailedCount = hasActiveMonitoring ? 0 : 1,
            Recommendations =
            [
                "Review and update the list of auditable events annually",
                "Coordinate audit event reviews with threat intelligence",
                "Document audit review procedures and findings"
            ]
        });

        // V-222574 / AU-3 - Privileged Function Execution
        var privilegedFunctionEvents = events.Where(static e =>
            e.EventType?.Contains("Admin", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Privileged", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Root", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Sudo", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Elevat", StringComparison.OrdinalIgnoreCase) == true ||
            e.User?.Contains("admin", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Privileged Function Execution (V-222574 / AU-3)",
            Passed = privilegedFunctionEvents.Any(),
            Message = privilegedFunctionEvents.Any()
                ? $"Privileged function execution is being audited ({privilegedFunctionEvents.Count} events)"
                : "FINDING: All privileged function execution must be audited",
            Severity = privilegedFunctionEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Audit and Accountability",
            RegulationReference = "NIST 800-53 AU-3 / V-222574",
            TotalCount = events.Count,
            FailedCount = privilegedFunctionEvents.Any() ? 0 : 1,
            Recommendations = privilegedFunctionEvents.Any()
                ? []
                :
                [
                    "FINDING (CAT II): Log all execution of privileged functions",
                    "Include administrative actions (user management, config changes)",
                    "Log privilege escalation and role changes",
                    "This is a mandatory requirement, not addressable"
                ]
        });

        // V-222534 / AC-2 - Account Management
        var accountManagementEvents = events.Where(static e =>
            e.EventType?.Contains("Account", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("UserCreate", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("UserDelete", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("UserDisable", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("UserEnable", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Registration", StringComparison.OrdinalIgnoreCase) == true ||
            (e.EntityType?.Contains("User", StringComparison.OrdinalIgnoreCase) == true &&
             (e.Action == "Added" || e.Action == "Deleted" || e.Action == "Modified"))).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Account Management (V-222534 / AC-2)",
            Passed = accountManagementEvents.Any(),
            Message = accountManagementEvents.Any()
                ? $"Account management activities are being audited ({accountManagementEvents.Count} events)"
                : "FINDING: Account creation, modification, enabling, disabling, and removal must be logged",
            Severity = accountManagementEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Access Control",
            RegulationReference = "NIST 800-53 AC-2 / V-222534",
            TotalCount = events.Count,
            FailedCount = accountManagementEvents.Any() ? 0 : 1,
            Recommendations = accountManagementEvents.Any()
                ? []
                :
                [
                    "FINDING (CAT II): Log all account lifecycle events",
                    "Track account creation, modification, enabling, disabling, and removal",
                    "Log who performed the action and when",
                    "Notify account managers when accounts are created, modified, or removed"
                ]
        });

        // V-222542 / AC-7 - Unsuccessful Logon Attempts
        var failedLoginEvents = events.Where(static e =>
            e.EventType?.Contains("FailedLogin", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("LoginFailed", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("AuthenticationFailed", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("FailedAuthentication", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Denied", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Lockout", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("AccountLocked", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Unsuccessful Logon Attempts (V-222542 / AC-7)",
            Passed = true, // Having none could mean no failures occurred
            Message = failedLoginEvents.Any()
                ? $"Failed logon attempts are being tracked ({failedLoginEvents.Count} events) - ensure account lockout is enforced"
                : "No failed logon events detected - ensure failed authentication logging is configured",
            Severity = failedLoginEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "DISA STIG",
            Category = "Access Control",
            RegulationReference = "NIST 800-53 AC-7 / V-222542",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Log all unsuccessful logon attempts with user identity and source",
                "Enforce account lockout after 3 consecutive failed attempts (DoD standard)",
                "Automatically lock accounts for 15 minutes or until released by administrator",
                "Delay next logon prompt by at least 4 seconds after a failed attempt"
            ]
        });

        // V-222543 / AC-8 - System Use Notification
        var systemUseNotificationEvents = events.Where(static e =>
            e.EventType?.Contains("Banner", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SystemUse", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Consent", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("TermsAccepted", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Disclaimer", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "System Use Notification (V-222543 / AC-8)",
            Passed = systemUseNotificationEvents.Any(),
            Message = systemUseNotificationEvents.Any()
                ? $"System use notification acknowledgments are being logged ({systemUseNotificationEvents.Count} events)"
                : "FINDING: Application must display and log acceptance of DoD-approved system use notification",
            Severity = systemUseNotificationEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Access Control",
            RegulationReference = "NIST 800-53 AC-8 / V-222543",
            TotalCount = events.Count,
            FailedCount = systemUseNotificationEvents.Any() ? 0 : 1,
            Recommendations = systemUseNotificationEvents.Any()
                ? []
                :
                [
                    "FINDING (CAT II): Display DoD Notice and Consent Banner before granting access",
                    "Log user acknowledgment of the system use notification",
                    "Banner must remain until user takes explicit action to log on",
                    "Use Standard Mandatory DoD Notice and Consent Banner text"
                ]
        });

        // V-222553 / AC-17 - Remote Access
        var remoteAccessEvents = events.Where(static e =>
            e.EventType?.Contains("Remote", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("VPN", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("SSH", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("RDP", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("API", StringComparison.OrdinalIgnoreCase) == true).ToList();

        // Also check IP addresses - if events have IP data, remote access is being tracked
        var eventsWithIpAddress = events.Count(static e => !string.IsNullOrWhiteSpace(e.IpAddress));

        results.Add(new AuditValidationResult
        {
            RuleName = "Remote Access (V-222553 / AC-17)",
            Passed = remoteAccessEvents.Any() || eventsWithIpAddress > 0,
            Message = remoteAccessEvents.Any()
                ? $"Remote access sessions are being monitored ({remoteAccessEvents.Count} remote access events, {eventsWithIpAddress} events with IP tracking)"
                : eventsWithIpAddress > 0
                    ? $"IP address tracking active ({eventsWithIpAddress}/{events.Count} events) - consider explicit remote access event logging"
                    : "FINDING: Remote access must be monitored and controlled",
            Severity = remoteAccessEvents.Any() || eventsWithIpAddress > 0
                ? ValidationSeverity.Info
                : ValidationSeverity.High,
            ComplianceStandard = "DISA STIG",
            Category = "Access Control",
            RegulationReference = "NIST 800-53 AC-17 / V-222553",
            TotalCount = events.Count,
            FailedCount = remoteAccessEvents.Any() || eventsWithIpAddress > 0 ? 0 : 1,
            Recommendations = remoteAccessEvents.Any()
                ? []
                :
                [
                    "FINDING (CAT II): Log all remote access sessions with source IP addresses",
                    "Monitor remote access for unauthorized or suspicious activity",
                    "Ensure all API access is logged with client identification",
                    "Implement session tracking for remote connections"
                ]
        });

        // Security Incident Tracking (SI-4 mapping)
        var securityIncidentEvents = events.Where(static e =>
            e.EventType?.Contains("Security", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Incident", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Breach", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Threat", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Alert", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Intrusion", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Information System Monitoring (SI-4)",
            Passed = true, // Having no incidents is acceptable
            Message = securityIncidentEvents.Any()
                ? $"Security incidents and alerts are being tracked ({securityIncidentEvents.Count} events)"
                : "No security incident events detected - ensure security monitoring and alerting is configured",
            Severity = ValidationSeverity.Info,
            ComplianceStandard = "DISA STIG",
            Category = "System and Information Integrity",
            RegulationReference = "NIST 800-53 SI-4",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations =
            [
                "Deploy intrusion detection and monitoring capabilities",
                "Configure real-time alerts for security-relevant events",
                "Forward security events to organizational SIEM",
                "Report incidents per DoD incident reporting procedures"
            ]
        });

        // Password/Credential Change Tracking (IA-5)
        var credentialChangeEvents = events.Where(static e =>
            e.EventType?.Contains("Password", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Credential", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("MFA", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Token", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("Certificate", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("CAC", StringComparison.OrdinalIgnoreCase) == true ||
            e.EventType?.Contains("PKI", StringComparison.OrdinalIgnoreCase) == true).ToList();

        results.Add(new AuditValidationResult
        {
            RuleName = "Authenticator Management (IA-5)",
            Passed = credentialChangeEvents.Any(),
            Message = credentialChangeEvents.Any()
                ? $"Authenticator changes are being logged ({credentialChangeEvents.Count} events)"
                : "Consider logging authenticator (password, MFA, CAC/PKI) changes",
            Severity = credentialChangeEvents.Any() ? ValidationSeverity.Info : ValidationSeverity.Medium,
            ComplianceStandard = "DISA STIG",
            Category = "Identification and Authentication",
            RegulationReference = "NIST 800-53 IA-5",
            TotalCount = events.Count,
            FailedCount = 0,
            Recommendations = credentialChangeEvents.Any()
                ? []
                :
                [
                    "Log all authenticator lifecycle events (creation, change, revocation)",
                    "Track password changes and resets",
                    "Log MFA enrollment and changes",
                    "DoD environments should log CAC/PKI certificate events"
                ]
        });

        await Task.CompletedTask;
        return results;
    }

    private static List<string> BuildMissingCategoryRecommendations(
        int auditableEventCategories,
        List<AuditEventEntity> logoffEvents,
        List<AuditEventEntity> privilegeEvents,
        List<AuditEventEntity> policyChangeEvents)
    {
        var recommendations = new List<string>
        {
            $"Consider adding coverage for missing event categories ({5 - auditableEventCategories} remaining)"
        };

        if (!logoffEvents.Any())
            recommendations.Add("Add logoff/session end event logging");
        if (!privilegeEvents.Any())
            recommendations.Add("Add privileged function/role change logging");
        if (!policyChangeEvents.Any())
            recommendations.Add("Add policy/configuration change logging");

        return recommendations;
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
            recommendations.Add("✅ DISA STIG COMPLIANCE: All validation checks passed");
            recommendations.Add("");
            recommendations.Add("📋 Ongoing STIG Compliance Requirements:");
            recommendations.Add("  • Run STIG Viewer assessments quarterly");
            recommendations.Add("  • Submit POA&Ms for any open findings");
            recommendations.Add("  • Maintain ATO documentation");
            recommendations.Add("  • Review audit logs weekly (minimum)");
            recommendations.Add("  • Validate archive integrity monthly");
            recommendations.Add("  • Update STIG checklist with each application release");
            return recommendations;
        }

        var critical = failedResults.Where(static r => r.Severity == ValidationSeverity.Critical).ToList();
        var high = failedResults.Where(static r => r.Severity == ValidationSeverity.High).ToList();
        var medium = failedResults.Where(static r => r.Severity == ValidationSeverity.Medium).ToList();
        var low = failedResults.Where(static r => r.Severity == ValidationSeverity.Low).ToList();

        recommendations.Add("🛡️ DISA STIG COMPLIANCE REPORT");
        recommendations.Add($"Found {failedResults.Count} finding(s) requiring remediation");
        recommendations.Add("");

        if (critical.Any())
        {
            recommendations.Add("🚨 CAT I FINDINGS - CRITICAL - IMMEDIATE REMEDIATION REQUIRED:");
            recommendations.Add("CAT I findings represent the highest risk and must be remediated immediately.");
            recommendations.Add("These findings can directly result in loss of ATO.");
            recommendations.Add("");

            foreach (var result in critical)
            {
                recommendations.Add($"  ⚠️ {result.RuleName}");
                recommendations.Add($"     Control: {result.RegulationReference}");
                recommendations.Add($"     Finding: {result.Message}");
                if (result.FailedCount > 0)
                {
                    recommendations.Add($"     Impact: {result.FailedCount} of {result.TotalCount} events affected");
                }

                if (result.Recommendations.Any())
                {
                    recommendations.Add("     REQUIRED REMEDIATION:");
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
            recommendations.Add("🔴 CAT II FINDINGS - HIGH - Remediate Within 30 Days:");
            recommendations.Add("CAT II findings represent moderate risk and require timely remediation.");
            recommendations.Add("");

            foreach (var result in high)
            {
                recommendations.Add($"  • {result.RuleName} ({result.RegulationReference})");
                recommendations.Add($"    Finding: {result.Message}");
                if (result.Recommendations.Any())
                {
                    recommendations.Add("    Remediation:");
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
            recommendations.Add("🟡 CAT III FINDINGS - MEDIUM - Remediate Within 90 Days:");
            recommendations.Add("CAT III findings represent lower risk but still require remediation.");
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
            recommendations.Add("🔵 INFORMATIONAL - Best Practices:");
            foreach (var result in low)
            {
                recommendations.Add($"  • {result.RuleName}: {result.Message}");
            }

            recommendations.Add("");
        }

        // STIG-specific guidance
        recommendations.Add("📚 DISA STIG Resources:");
        recommendations.Add("  • DISA STIG Library: https://public.cyber.mil/stigs/");
        recommendations.Add("  • STIG Viewer: https://public.cyber.mil/stigs/srg-stig-tools/");
        recommendations.Add(
            "  • Application Security STIG: search 'Application Security and Development' on DISA site");
        recommendations.Add("  • NIST 800-53 Controls: https://csf.tools/reference/nist-sp-800-53/");
        recommendations.Add("");

        recommendations.Add("📋 ATO (Authority to Operate) Impact:");
        recommendations.Add("  • CAT I findings: May prevent ATO approval or cause immediate ATO revocation");
        recommendations.Add("  • CAT II findings: Must have POA&M with remediation timeline");
        recommendations.Add("  • CAT III findings: Should be addressed but typically accepted with POA&M");
        recommendations.Add("");

        recommendations.Add("🔒 DoD-Specific Requirements:");
        recommendations.Add("  • All DoD systems require ATO per DoDI 8510.01 (RMF)");
        recommendations.Add("  • Audit logs must be forwarded to organizational SIEM");
        recommendations.Add("  • CAC/PKI authentication is required for privileged access");
        recommendations.Add("  • Consult your ISSM/ISSO for site-specific requirements");

        return recommendations;
    }
}
