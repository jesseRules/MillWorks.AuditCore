using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing the result of an audit archival operation.
/// </summary>
public sealed class AuditArchivalResult
{
    /// <summary>
    /// Identifier of the created archive.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// Indicates whether the archival operation was successful.
    /// </summary>
    [Required]
    [Display(Name = "Success")]
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>
    /// Message describing the result of the archival operation.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Number of events that were archived.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    /// <summary>
    /// Size of the created archive in bytes.
    /// </summary>
    [Required]
    [Range(0, long.MaxValue)]
    [Display(Name = "Archive Size")]
    [JsonPropertyName("archive_size")]
    public long ArchiveSize { get; set; }

    /// <summary>
    /// UTC timestamp when the archival operation started.
    /// </summary>
    [Required]
    [Display(Name = "Start Time")]
    [JsonPropertyName("start_time")]
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// UTC timestamp when the archival operation completed.
    /// </summary>
    [Required]
    [Display(Name = "End Time")]
    [JsonPropertyName("end_time")]
    public DateTimeOffset EndTime { get; set; }
}