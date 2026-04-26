using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Abstractions.Attributes;

/// <summary>
/// Marks a property as containing sensitive data (PII, PHI, etc.)
/// This is used for FERPA, HIPAA, and other compliance requirements
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class SensitiveDataAttribute : Attribute
{
    /// <summary>
    /// Type of sensitive data
    /// </summary>
    public SensitiveDataType DataType { get; set; } = SensitiveDataType.PersonalIdentifier;

    /// <summary>
    /// Compliance standards that apply to this data
    /// </summary>
    public ComplianceStandard[] ApplicableStandards { get; set; } = [];

    /// <summary>
    /// Whether to automatically encrypt this field
    /// </summary>
    public bool AutoEncrypt { get; set; } = true;

    /// <summary>
    /// Whether to mask this field in logs (instead of encrypting)
    /// </summary>
    public bool MaskInLogs { get; set; } = false;

    /// <summary>
    /// Custom mask pattern (e.g., "XXX-XX-{4}" for SSN).
    /// Must be a static replacement string — not a format template that includes real data.
    /// Maximum 50 characters. If null, defaults to "***".
    /// </summary>
    public string? MaskPattern
    {
        get => _maskPattern;
        set
        {
            if (value is { Length: > 50 })
                throw new ArgumentException(
                    "MaskPattern must be 50 characters or fewer. Long patterns risk leaking data.");
            _maskPattern = value;
        }
    }

    /// <summary>
    /// Backing field for MaskPattern with validation to prevent excessively long patterns that could leak data.
    /// </summary>
    private string? _maskPattern;
}
