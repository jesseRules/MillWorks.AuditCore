using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.EntityFramework.Dto;

/// <summary>
/// DTO representing an archived collection of audit events with integrity records.
/// </summary>
public sealed class AuditArchive
{
    /// <summary>
    /// Unique identifier for this audit archive.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when this archive was created.
    /// </summary>
    [Required]
    [Display(Name = "Created At")]
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Start date of the events included in this archive.
    /// </summary>
    [Required]
    [Display(Name = "Date Range Start")]
    [JsonPropertyName("date_range_start")]
    public DateTimeOffset DateRangeStart { get; set; }

    /// <summary>
    /// End date of the events included in this archive.
    /// </summary>
    [Required]
    [Display(Name = "Date Range End")]
    [JsonPropertyName("date_range_end")]
    public DateTimeOffset DateRangeEnd { get; set; }

    /// <summary>
    /// Number of audit events contained in this archive.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    /// <summary>
    /// List of audit events included in this archive.
    /// </summary>
    [Display(Name = "Events")]
    [JsonPropertyName("events")]
    public List<AuditEventDto> Events { get; set; } = new();

    /// <summary>
    /// List of integrity records for verifying archive authenticity.
    /// </summary>
    [Display(Name = "Integrity Records")]
    [JsonPropertyName("integrity_records")]
    public List<AuditIntegrityDto> IntegrityRecords { get; set; } = new();

    /// <summary>
    /// Metadata about this archive including version and hash information.
    /// </summary>
    [Required]
    [Display(Name = "Metadata")]
    [JsonPropertyName("metadata")]
    public ArchiveMetadata Metadata { get; set; } = new();
}