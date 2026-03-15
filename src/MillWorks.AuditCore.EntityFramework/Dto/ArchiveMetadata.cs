using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.EntityFramework.Dto;

/// <summary>
/// DTO containing metadata about an audit archive for identification and integrity verification.
/// </summary>
public sealed class ArchiveMetadata
{
    /// <summary>
    /// Unique identifier for this archive.
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// Version of the archive format used.
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Display(Name = "Archive Version")]
    [JsonPropertyName("archive_version")]
    public string ArchiveVersion { get; set; } = string.Empty;

    /// <summary>
    /// Type of compression used for the archive.
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Display(Name = "Compression Type")]
    [JsonPropertyName("compression_type")]
    public string CompressionType { get; set; } = string.Empty;

    /// <summary>
    /// Cryptographic hash of the archive for integrity verification.
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Display(Name = "Archive Hash")]
    [JsonPropertyName("archive_hash")]
    public string ArchiveHash { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when this archive was created.
    /// </summary>
    [Required]
    [Display(Name = "Created At")]
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Number of events contained in this archive.
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    /// <summary>
    /// Start date of the events in this archive.
    /// </summary>
    [Required]
    [Display(Name = "Date Range Start")]
    [JsonPropertyName("date_range_start")]
    public DateTimeOffset DateRangeStart { get; set; }

    /// <summary>
    /// End date of the events in this archive.
    /// </summary>
    [Required]
    [Display(Name = "Date Range End")]
    [JsonPropertyName("date_range_end")]
    public DateTimeOffset DateRangeEnd { get; set; }

    /// <summary>
    /// Size of the archive file in bytes.
    /// </summary>
    [Required]
    [Range(0, long.MaxValue)]
    [Display(Name = "Size (Bytes)")]
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Current status of the archive.
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Display(Name = "Status")]
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;
}