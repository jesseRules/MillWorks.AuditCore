using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Severity levels for audit validation results
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ValidationSeverity
{
    /// <summary>
    /// Informational - no action required
    /// </summary>
    Info = 0,

    /// <summary>
    /// Low severity - minor issue that should be addressed
    /// </summary>
    Low = 1,

    /// <summary>
    /// Medium severity - should be addressed soon
    /// </summary>
    Medium = 2,

    /// <summary>
    /// High severity - should be addressed immediately
    /// </summary>
    High = 3,

    /// <summary>
    /// Critical severity - requires immediate action, compliance at risk
    /// </summary>
    Critical = 4
}