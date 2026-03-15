namespace MillWorks.AuditCore.Abstractions.Constants;

/// <summary>
/// Cross-cutting event type constants shared by all compliance validators.
/// Source of truth for event type keywords used across FERPA, HIPAA, SOC2, PCI-DSS, STIG, etc.
/// </summary>
public static class ComplianceEventTypes
{
    // Authentication (used by FERPA, HIPAA, SOC2, PCI-DSS, STIG)
    public const string Login = "Login";
    public const string Authentication = "Authentication";
    public const string SignIn = "SignIn";

    // Security (used by all validators)
    public const string Security = "Security";
    public const string Breach = "Breach";
    public const string Incident = "Incident";
    public const string Violation = "Violation";

    // Consent / Authorization (used by FERPA, GDPR, HIPAA)
    public const string Consent = "Consent";
    public const string Authorization = "Authorization";

    // Data operations (used by GDPR, FERPA)
    public const string Disclosure = "Disclosure";
    public const string DataSharing = "DataSharing";
    public const string ThirdParty = "ThirdParty";
    public const string DeIdentified = "DeIdentified";
    public const string Anonymized = "Anonymized";
}
