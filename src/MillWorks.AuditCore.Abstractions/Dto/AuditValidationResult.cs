using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing the result of a specific compliance rule validation.
/// </summary>
public sealed class AuditValidationResult
{
    /// <summary>
    /// Unique identifier for this validation result
    /// </summary>
    [Display(Name = "ID")]
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Name of the compliance rule that was validated.
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Display(Name = "Rule Name")]
    [JsonPropertyName("rule_name")]
    public string RuleName { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the rule validation passed.
    /// </summary>
    [Required]
    [Display(Name = "Passed")]
    [JsonPropertyName("passed")]
    public bool Passed { get; set; }

    /// <summary>
    /// Message describing the validation result.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Severity level of the validation result.
    /// </summary>
    [Required]
    [Display(Name = "Severity")]
    [JsonPropertyName("severity")]
    public ValidationSeverity Severity { get; set; } = ValidationSeverity.Medium;

    /// <summary>
    /// Compliance standard this validation is for (GDPR, HIPAA, etc.)
    /// </summary>
    [MaxLength(50)]
    [Display(Name = "Compliance Standard")]
    [JsonPropertyName("compliance_standard")]
    public string? ComplianceStandard { get; set; }

    /// <summary>
    /// Category of the validation rule (Access Control, Data Retention, etc.)
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Category")]
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>
    /// Specific regulation or requirement reference (e.g., "GDPR Article 30")
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Regulation Reference")]
    [JsonPropertyName("regulation_reference")]
    public string? RegulationReference { get; set; }

    /// <summary>
    /// Number of events that failed this validation
    /// </summary>
    [Display(Name = "Failed Count")]
    [JsonPropertyName("failed_count")]
    public int FailedCount { get; set; }

    /// <summary>
    /// Total number of events checked
    /// </summary>
    [Display(Name = "Total Count")]
    [JsonPropertyName("total_count")]
    public int TotalCount { get; set; }

    /// <summary>
    /// When this validation was performed
    /// </summary>
    [Display(Name = "Validated At")]
    [JsonPropertyName("validated_at")]
    public DateTimeOffset ValidatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Recommended actions to remediate the issue
    /// </summary>
    [Display(Name = "Recommendations")]
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Gets a human-readable severity description
    /// </summary>
    [JsonIgnore]
    public string SeverityDescription => Severity switch
    {
        ValidationSeverity.Info => "Informational - No action required",
        ValidationSeverity.Low => "Low - Should be addressed when convenient",
        ValidationSeverity.Medium => "Medium - Should be addressed soon",
        ValidationSeverity.High => "High - Requires immediate attention",
        ValidationSeverity.Critical => "Critical - Compliance at risk, immediate action required",
        _ => "Unknown"
    };

    /// <summary>
    /// Gets the pass percentage
    /// </summary>
    [JsonIgnore]
    public double PassPercentage => TotalCount > 0 
        ? Math.Round((double)(TotalCount - FailedCount) / TotalCount * 100, 2) 
        : 0;

    /// <summary>
    /// Returns a string representation of this validation result
    /// </summary>
    public override string ToString()
    {
        return $"{RuleName}: {(Passed ? "PASSED" : "FAILED")} [{Severity}] - {Message}";
    }
}