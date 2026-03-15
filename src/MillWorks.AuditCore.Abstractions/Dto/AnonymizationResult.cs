using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing the result of a user data anonymization operation.
/// </summary>
public sealed class AnonymizationResult
{
    /// <summary>
    /// Identifier of the user whose data was anonymized.
    /// </summary>
    [Required]
    [Display(Name = "User ID")]
    [JsonPropertyName("user_id")]
    public Guid UserId { get; set; }

    /// <summary>
    /// Indicates whether the anonymization operation was successful.
    /// </summary>
    [Required]
    [Display(Name = "Success")]
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result of the anonymization operation.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Number of events that were anonymized.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }
}