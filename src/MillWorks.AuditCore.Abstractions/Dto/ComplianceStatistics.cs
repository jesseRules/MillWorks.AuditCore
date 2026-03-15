using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO containing statistical information about compliance evaluation events.
/// </summary>
public sealed class ComplianceStatistics
{
    /// <summary>
    /// Total number of audit events evaluated.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Total Events")]
    [JsonPropertyName("total_events")]
    public int TotalEvents { get; set; }

    /// <summary>
    /// Number of user access related events.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "User Access Events")]
    [JsonPropertyName("user_access_events")]
    public int UserAccessEvents { get; set; }

    /// <summary>
    /// Number of data modification events.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Data Modification Events")]
    [JsonPropertyName("data_modification_events")]
    public int DataModificationEvents { get; set; }

    /// <summary>
    /// Number of security-related events.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Security Events")]
    [JsonPropertyName("security_events")]
    public int SecurityEvents { get; set; }

    /// <summary>
    /// Number of events classified as high risk.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "High Risk Events")]
    [JsonPropertyName("high_risk_events")]
    public int HighRiskEvents { get; set; }
}