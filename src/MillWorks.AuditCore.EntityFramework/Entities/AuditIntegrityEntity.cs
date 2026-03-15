using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Enhanced AuditIntegrityEntity with proper EF attributes for migrations
/// </summary>
[NoAudit]
[Table("AuditIntegrity", Schema = "audit")]
[Index(nameof(EventId), IsUnique = true, Name = "IX_AuditIntegrity_EventId")]
[Index(nameof(SequenceNumber), IsUnique = true, Name = "IX_AuditIntegrity_SequenceNumber")]
[Index(nameof(TrustedTimestamp), Name = "IX_AuditIntegrity_Timestamp")]
[Index(nameof(EventHash), nameof(PreviousEventHash), Name = "IX_AuditIntegrity_HashChain")]
public class AuditIntegrityEntity: AppendOnlyEntity
{
    /// <summary>
    /// Event identifier - foreign key to AuditEventEntity
    /// </summary>
    [Required]
    [Column("EventId", TypeName = "uniqueidentifier")]
    public Guid EventId { get; set; }
    
    /// <summary>
    /// SHA-256 hash of the audit event data (Base64-encoded, 44 chars)
    /// </summary>
    [Required]
    [MaxLength(44)]
    [Column("EventHash", TypeName = "varchar(44)")]
    public string EventHash { get; set; } = string.Empty;
    
    /// <summary>
    /// Hash of the previous audit event (blockchain-style chaining, Base64-encoded)
    /// </summary>
    [MaxLength(44)]
    [Column("PreviousEventHash", TypeName = "varchar(44)")]
    public string? PreviousEventHash { get; set; }
    
    /// <summary>
    /// Digital signature of the event hash (if using PKI)
    /// </summary>
    [MaxLength(512)]
    [Column("DigitalSignature", TypeName = "varchar(512)")]
    public string? DigitalSignature { get; set; }
    
    /// <summary>
    /// Timestamp from a trusted time source
    /// </summary>
    [Required]
    [Column("TrustedTimestamp", TypeName = "datetimeoffset")]
    public DateTimeOffset TrustedTimestamp { get; set; }
    
    /// <summary>
    /// Sequence number for ordering verification - use IDENTITY for auto-increment
    /// </summary>
    [Required]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    [Column("SequenceNumber", TypeName = "bigint")]
    public long SequenceNumber { get; set; }
    
    /// <summary>
    /// HMAC for additional integrity verification (Base64-encoded, 44 chars)
    /// </summary>
    [MaxLength(44)]
    [Column("HmacSignature", TypeName = "varchar(44)")]
    public string? HmacSignature { get; set; }
    
    /// <summary>
    /// Checksum of critical fields
    /// </summary>
    [Required]
    [MaxLength(44)]
    [Column("Checksum", TypeName = "varchar(44)")]
    public string Checksum { get; set; } = string.Empty;
    
    /// <summary>
    /// Version of the integrity algorithm used
    /// </summary>
    [Required]
    [Column("AlgorithmVersion", TypeName = "int")]
    [DefaultValue(1)]
    public int AlgorithmVersion { get; set; } = 1;
    
    /// <summary>
    /// Parameters for the entity, typically used for additional configuration or metadata.
    /// </summary>
    [Display(Name = "Parameters")]
    [Column("Parameters")]
    [MaxLength(2000)]
    public string? Parameters { get; set; }

    // Navigation property
    /// <summary>
    /// Associated audit event
    /// </summary>
    [ForeignKey(nameof(EventId))]
    public virtual AuditEventEntity? AuditEvent { get; set; }
}
