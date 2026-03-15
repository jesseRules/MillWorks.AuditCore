namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Enumeration of supported compliance standards.
/// Canonical definition — all assemblies must reference this enum.
/// </summary>
public enum ComplianceStandard
{
    /// <summary>
    /// General Data Protection Regulation.
    /// </summary>
    GDPR = 0,

    /// <summary>
    /// Service Organization Control 2.
    /// </summary>
    SOC2 = 1,

    /// <summary>
    /// Health Insurance Portability and Accountability Act.
    /// </summary>
    HIPAA = 2,

    /// <summary>
    /// ISO/IEC 27001 Information Security Management.
    /// </summary>
    ISO27001 = 3,

    /// <summary>
    /// Family Educational Rights and Privacy Act.
    /// </summary>
    FERPA = 4,

    /// <summary>
    /// Payment Card Industry Data Security Standard.
    /// </summary>
    PCI_DSS = 5,

    /// <summary>
    /// DISA Security Technical Implementation Guide.
    /// </summary>
    STIG = 6,

    /// <summary>
    /// National Institute of Standards and Technology.
    /// </summary>
    NIST = 7
}