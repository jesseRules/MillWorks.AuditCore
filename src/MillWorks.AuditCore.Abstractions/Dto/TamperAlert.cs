using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing a tamper detection alert that requires attention.
/// </summary>
public sealed class TamperAlert
{
    /// <summary>
    /// Optional identifier of the specific event that triggered this alert.
    /// </summary>
    [Display(Name = "Event ID")]
    [JsonPropertyName("event_id")]
    public Guid? EventId { get; set; }

    /// <summary>
    /// Type of tampering alert (e.g., "hash_mismatch", "chain_break", "missing_event").
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Alert Type")]
    [JsonPropertyName("alert_type")]
    public string AlertType { get; set; } = string.Empty;

    /// <summary>
    /// Detailed description of the tampering alert.
    /// </summary>
    [Required]
    [MaxLength(1000)]
    [Display(Name = "Description")]
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the tampering was detected.
    /// </summary>
    [Required]
    [Display(Name = "Detected At")]
    [JsonPropertyName("detected_at")]
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>
    /// Severity level of this tamper alert.
    /// </summary>
    [Required]
    [Display(Name = "Severity")]
    [JsonPropertyName("severity")]
    public TamperSeverity Severity { get; set; }
}