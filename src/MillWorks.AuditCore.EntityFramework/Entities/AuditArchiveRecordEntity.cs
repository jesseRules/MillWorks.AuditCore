using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Entity representing an archived audit collection record stored in the database
/// </summary>
// Table name and schema for this entity are configured fluently in
// AuditDbContext.ConfigureAudit (actual table name is "ArchiveRecord",
// singular); schema comes from HasDefaultSchema(EntityFrameworkOptions.Schema).
[Index(nameof(ArchiveId), IsUnique = true)]
[Index(nameof(CreatedAt))]
[Index(nameof(Status))]
[Index(nameof(DateRangeStart), nameof(DateRangeEnd))]
public sealed class AuditArchiveRecordEntity : AuditAggregateRoot
{
    /// <summary>
    /// Unique identifier for this archive (matches the blob name identifier)
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Archive ID")]
    [Column("ArchiveId", TypeName = "varchar(100)")]
    [JsonPropertyName("archive_id")]
    public string ArchiveId { get; set; } = string.Empty;

    /// <summary>
    /// Name of the blob storage container or file where the archive is stored
    /// </summary>
    [Required]
    [MaxLength(255)]
    [Display(Name = "Blob Name")]
    [Column("BlobName", TypeName = "varchar(255)")]
    [JsonPropertyName("blob_name")]
    public string BlobName { get; set; } = string.Empty;

    /// <summary>
    /// Azure Blob Storage container name
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Container Name")]
    [Column("ContainerName", TypeName = "varchar(100)")]
    [JsonPropertyName("container_name")]
    public string ContainerName { get; set; } = string.Empty;

    /// <summary>
    /// Number of events in this archive
    /// </summary>
    [Required]
    [Range(0, int.MaxValue)]
    [Display(Name = "Event Count")]
    [Column("EventCount")]
    [JsonPropertyName("event_count")]
    public int EventCount { get; set; }

    /// <summary>
    /// Start date of the archived events
    /// </summary>
    [Required]
    [Display(Name = "Date Range Start")]
    [Column("DateRangeStart", TypeName = "datetimeoffset")]
    [JsonPropertyName("date_range_start")]
    public DateTimeOffset DateRangeStart { get; set; }

    /// <summary>
    /// End date of the archived events
    /// </summary>
    [Required]
    [Display(Name = "Date Range End")]
    [Column("DateRangeEnd", TypeName = "datetimeoffset")]
    [JsonPropertyName("date_range_end")]
    public DateTimeOffset DateRangeEnd { get; set; }

    /// <summary>
    /// Size of the archive in bytes
    /// </summary>
    [Required]
    [Range(0, long.MaxValue)]
    [Display(Name = "Size (Bytes)")]
    [Column("SizeBytes")]
    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }

    /// <summary>
    /// Cryptographic hash of the archive for integrity verification
    /// </summary>
    [Required]
    [MaxLength(128)]
    [Display(Name = "Hash")]
    [Column("Hash", TypeName = "varchar(128)")]
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    /// <summary>
    /// Current status of the archive
    /// </summary>
    [Required]
    [Display(Name = "Status")]
    [Column("Status")]
    [JsonPropertyName("status")]
    public MillWorksArchiveStatus Status { get; set; } = MillWorksArchiveStatus.InProgress;

    /// <summary>
    /// Archive format version for compatibility tracking
    /// </summary>
    [Required]
    [MaxLength(20)]
    [Display(Name = "Archive Version")]
    [Column("ArchiveVersion", TypeName = "varchar(20)")]
    [JsonPropertyName("archive_version")]
    public string ArchiveVersion { get; set; } = "1.0";

    /// <summary>
    /// Compression algorithm used
    /// </summary>
    [Required]
    [MaxLength(50)]
    [Display(Name = "Compression Type")]
    [Column("CompressionType", TypeName = "varchar(50)")]
    [JsonPropertyName("compression_type")]
    public string CompressionType { get; set; } = "gzip";

    /// <summary>
    /// Error message if archive creation failed
    /// </summary>
    [MaxLength(1000)]
    [Display(Name = "Error Message")]
    [Column("ErrorMessage", TypeName = "nvarchar(1000)")]
    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Additional metadata stored as JSON
    /// </summary>
    [Display(Name = "Metadata")]
    [Column("Metadata")]
    [JsonPropertyName("metadata")]
    [MaxLength(4000)]
    public string? Metadata { get; set; }

    /// <summary>
    /// When the archive was last verified for integrity
    /// </summary>
    [Display(Name = "Last Verified At")]
    [Column("LastVerifiedAt", TypeName = "datetimeoffset")]
    [JsonPropertyName("last_verified_at")]
    public DateTimeOffset? LastVerifiedAt { get; set; }

    /// <summary>
    /// User who initiated the archive operation
    /// </summary>
    [MaxLength(450)]
    [Display(Name = "Created By User ID")]
    [Column("CreatedByUserId", TypeName = "varchar(450)")]
    [JsonPropertyName("created_by_user_id")]
    public string? CreatedByUserId { get; set; }

}