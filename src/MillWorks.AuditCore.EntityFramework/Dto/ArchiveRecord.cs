using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.EntityFramework.Dto;

/// <summary>
/// DTO representing a record of an archived audit collection.
/// </summary>
public sealed class ArchiveRecord
{
    /// <summary>
    /// Unique identifier for this archive record.
    /// </summary>
    [Required]
    [Display(Name = "ID")]
    [JsonPropertyName("id")]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Identifier of the associated archive.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the blob storage container or file where the archive is stored.
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Display(Name = "Blob Name")]
    [JsonPropertyName("blob_name")]
    public string BlobName { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when this archive was created.
    /// </summary>
    [Required]
    [Display(Name = "Created At")]
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Number of events in this archive.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    /// <summary>
    /// Start date of the archived events.
    /// </summary>
    [Required]
    [Display(Name = "Date Range Start")]
    [JsonPropertyName("date_range_start")]
    public DateTimeOffset DateRangeStart { get; set; }

    /// <summary>
    /// End date of the archived events.
    /// </summary>
    [Required]
    [Display(Name = "Date Range End")]
    [JsonPropertyName("date_range_end")]
    public DateTimeOffset DateRangeEnd { get; set; }

    /// <summary>
    /// Size of the archive in bytes.
    /// </summary>
    [Required]
    [Range(0, long.MaxValue)]
    [Display(Name = "Size (Bytes)")]
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Cryptographic hash of the archive for integrity verification.
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Display(Name = "Hash")]
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the archive.
    /// </summary>
    [Required]
    [Display(Name = "Status")]
    [JsonPropertyName("status")]
    public MillWorksArchiveStatus Status { get; set; }
}