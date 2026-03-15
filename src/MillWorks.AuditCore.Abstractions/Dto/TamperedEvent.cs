using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing a specific event that has been identified as tampered or suspicious.
/// </summary>
public sealed class TamperedEvent
{
    /// <summary>
    /// Unique identifier of the tampered event.
    /// </summary>
    [Required]
    [Display(Name = "Event ID")]
    [JsonPropertyName("event_id")]
    public Guid EventId { get; set; }

    /// <summary>
    /// Reason why this event was flagged as tampered.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Reason")]
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the tampering was detected.
    /// </summary>
    [Required]
    [Display(Name = "Detected At")]
    [JsonPropertyName("detected_at")]
    public DateTimeOffset DetectedAt { get; set; }
}