using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing a compliance report for a specific standard and time period.
/// </summary>
public sealed class ComplianceReport
{
    /// <summary>
    /// The compliance standard this report is for.
    /// </summary>
    [Required]
    [Display(Name = "Standard")]
    [JsonPropertyName("standard")]
    public ComplianceStandard Standard { get; set; }

    /// <summary>
    /// UTC timestamp when this report was generated.
    /// </summary>
    [Required]
    [Display(Name = "Generated At")]
    [JsonPropertyName("generated_at")]
    public DateTimeOffset GeneratedAt { get; set; }

    /// <summary>
    /// Start date of the compliance evaluation period.
    /// </summary>
    [Required]
    [Display(Name = "Period Start")]
    [JsonPropertyName("period_start")]
    public DateTimeOffset PeriodStart { get; set; }

    /// <summary>
    /// End date of the compliance evaluation period.
    /// </summary>
    [Required]
    [Display(Name = "Period End")]
    [JsonPropertyName("period_end")]
    public DateTimeOffset PeriodEnd { get; set; }

    /// <summary>
    /// Indicates whether the organization is compliant with the standard.
    /// </summary>
    [Required]
    [Display(Name = "Is Compliant")]
    [JsonPropertyName("is_compliant")]
    public bool IsCompliant { get; set; }

    /// <summary>
    /// List of individual compliance rule validation results.
    /// </summary>
    [Display(Name = "Validation Results")]
    [JsonPropertyName("validation_results")]
    public List<AuditValidationResult> ValidationResults { get; set; } = new();

    /// <summary>
    /// List of recommendations for improving compliance.
    /// </summary>
    [Display(Name = "Recommendations")]
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    /// <summary>
    /// Statistical information about the compliance evaluation.
    /// </summary>
    [Required]
    [Display(Name = "Statistics")]
    [JsonPropertyName("statistics")]
    public ComplianceStatistics Statistics { get; set; } = new();
}