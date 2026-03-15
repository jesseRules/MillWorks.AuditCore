using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing the result of an audit archive restoration operation.
/// </summary>
public sealed class AuditRestoreResult
{
    /// <summary>
    /// Identifier of the archive that was restored.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the restore operation was successful.
    /// </summary>
    [Required]
    [Display(Name = "Success")]
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result of the restore operation.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Number of events that were successfully restored.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Restored Event Count")]
    [JsonPropertyName("restored_event_count")]
    public int RestoredEventCount { get; set; }
}