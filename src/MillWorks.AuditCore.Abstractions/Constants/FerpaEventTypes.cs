namespace MillWorks.AuditCore.Abstractions.Constants;

/// <summary>
/// FERPA-specific event type constants and re-exports of shared constants.
/// Validators and interceptors must reference these constants instead of raw strings.
/// </summary>
public static class FerpaEventTypes
{
    // Education records access (§99.10) — FERPA-specific
    public const string StudentAccess = "Student";
    public const string EducationRecord = "Education";
    public const string Record = "Record";
    public const string Enrollment = "Enrollment";
    public const string Grade = "Grade";
    public const string Transcript = "Transcript";

    // Consent (§99.30) — FERPA-specific
    public const string ParentConsent = "ParentConsent";

    // Directory information (§99.37) — FERPA-specific
    public const string DirectoryOptOut = "DirectoryOptOut";
    public const string OptOut = "OptOut";
    public const string DirectoryInfo = "DirectoryInfo";

    // Legitimate interest (§99.31(a)(1)) — FERPA-specific
    public const string LegitimateInterest = "LegitimateInterest";
    public const string AccessJustification = "AccessJustification";
    public const string EducationalPurpose = "EducationalPurpose";

    // Annual notification (§99.7) — FERPA-specific
    public const string FerpaNotification = "FerpaNotification";
    public const string AnnualNotice = "AnnualNotice";
    public const string RightsNotification = "RightsNotification";

    // FERPA interceptor prefix — FERPA-specific
    public const string FerpaPrefix = "FERPA.";

    /// <summary>
    /// Builds FERPA-prefixed event type strings for the audit interceptor.
    /// Format: "FERPA.{EntityName}.{Action}" (e.g., "FERPA.Student.Updated").
    /// This format is a contract between the interceptor and the validator —
    /// validators match on the FerpaPrefix constant to identify interceptor-generated events.
    /// </summary>
    public static class EventTypeBuilder
    {
        /// <summary>
        /// Builds a FERPA event type string: "FERPA.{entityName}.{action}"
        /// </summary>
        public static string Build(string entityName, string action)
            => $"{FerpaPrefix}{entityName}.{action}";
    }

    // Re-exports from ComplianceEventTypes for single-import convenience
    public const string Login = ComplianceEventTypes.Login;
    public const string Authentication = ComplianceEventTypes.Authentication;
    public const string SignIn = ComplianceEventTypes.SignIn;
    public const string Security = ComplianceEventTypes.Security;
    public const string Breach = ComplianceEventTypes.Breach;
    public const string Incident = ComplianceEventTypes.Incident;
    public const string Violation = ComplianceEventTypes.Violation;
    public const string Consent = ComplianceEventTypes.Consent;
    public const string Authorization = ComplianceEventTypes.Authorization;
    public const string Disclosure = ComplianceEventTypes.Disclosure;
    public const string DataSharing = ComplianceEventTypes.DataSharing;
    public const string ThirdParty = ComplianceEventTypes.ThirdParty;
    public const string DeIdentified = ComplianceEventTypes.DeIdentified;
    public const string Anonymized = ComplianceEventTypes.Anonymized;
}
