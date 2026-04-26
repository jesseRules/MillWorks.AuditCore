namespace MillWorks.AuditCore.Abstractions.Attributes;

/// <summary>
/// Types of sensitive data
/// </summary>
public enum SensitiveDataType
{
    /// <summary>
    /// Personal identifiers such as Social Security numbers or passport numbers
    /// </summary>
    PersonalIdentifier,

    /// <summary>
    /// Financial information such as credit card numbers or bank account details
    /// </summary>
    FinancialData,

    /// <summary>
    /// Health information such as medical records or health insurance details
    /// </summary>
    HealthInformation,

    /// <summary>
    /// Educational records such as grades or transcripts
    /// </summary>
    EducationalRecord,

    /// <summary>
    /// Biometric data such as fingerprints or facial recognition data
    /// </summary>
    BiometricData,

    /// <summary>
    /// Location data such as GPS coordinates or addresses
    /// </summary>
    LocationData,

    /// <summary>
    /// Communication data such as emails, messages, or call records
    /// </summary>
    CommunicationData,

    /// <summary>
    /// Other types of sensitive data not covered by the predefined categories
    /// </summary>
    Other
}
