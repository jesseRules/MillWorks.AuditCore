using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Durable outbox for pending integrity record creation. Inserted in the same transaction
/// as the audit event in batched mode, ensuring that pending integrity work survives
/// hard kills and process crashes. The IntegrityWriteBatcher consumes
/// these work items and marks them complete after creating the integrity record.
/// </summary>
[NoAudit]
[Table("AuditIntegrityWorkItems")]
[Index(nameof(Status), Name = "IX_IntegrityWorkItems_Status")]
[Index(nameof(EventId), IsUnique = true, Name = "IX_IntegrityWorkItems_EventId")]
[Index(nameof(LeaseExpiresAt), Name = "IX_IntegrityWorkItems_LeaseExpiry")]
[Index(nameof(Status), nameof(CreatedAt), Name = "IX_IntegrityWorkItems_Status_CreatedAt")]
public class AuditIntegrityWorkItemEntity : AuditEntity
{
    /// <summary>
    /// Foreign key to the audit event that needs an integrity record
    /// </summary>
    [Required]
    [Column("EventId", TypeName = "uniqueidentifier")]
    [JsonPropertyName("event_id")]
    [DisplayName("Event Id")]
    public Guid EventId { get; set; }

    /// <summary>
    /// Processing status of this work item
    /// </summary>
    [Required]
    [Column("Status")]
    [JsonPropertyName("status")]
    [DisplayName("Status")]
    public IntegrityStatus Status { get; set; } = IntegrityStatus.Pending;

    /// <summary>
    /// Number of processing attempts made
    /// </summary>
    [Required]
    [Column("AttemptCount")]
    [JsonPropertyName("attempt_count")]
    [DisplayName("Attempt Count")]
    public int AttemptCount { get; set; }

    /// <summary>
    /// When this work item was created (same transaction as the audit event)
    /// </summary>
    [Required]
    [Column("CreatedAt")]
    [JsonPropertyName("created_at")]
    [DisplayName("Created At")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When the last processing attempt occurred
    /// </summary>
    [Column("LastAttemptAt")]
    [JsonPropertyName("last_attempt_at")]
    [DisplayName("Last Attempt At")]
    public DateTimeOffset? LastAttemptAt { get; set; }

    /// <summary>
    /// Error message from the last failed attempt
    /// </summary>
    [MaxLength(2000)]
    [Column("LastError")]
    [JsonPropertyName("last_error")]
    [DisplayName("Last Error")]
    public string? LastError { get; set; }

    /// <summary>
    /// Identifier of the batcher instance that currently owns this work item.
    /// Used for lease-based concurrency in multi-instance deployments.
    /// </summary>
    [MaxLength(200)]
    [Column("LeaseOwner")]
    [JsonPropertyName("lease_owner")]
    [DisplayName("Lease Owner")]
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// When the current lease expires. Expired leases can be reclaimed by other instances.
    /// </summary>
    [Column("LeaseExpiresAt")]
    [JsonPropertyName("lease_expires_at")]
    [DisplayName("Lease Expires At")]
    public DateTimeOffset? LeaseExpiresAt { get; set; }

    /// <summary>
    /// When processing completed successfully
    /// </summary>
    [Column("CompletedAt")]
    [JsonPropertyName("completed_at")]
    [DisplayName("Completed At")]
    public DateTimeOffset? CompletedAt { get; set; }

    // ===== NAVIGATION PROPERTY =====

    /// <summary>
    /// Associated audit event
    /// </summary>
    [ForeignKey(nameof(EventId))]
    [JsonIgnore]
    public virtual AuditEventEntity? AuditEvent { get; set; }
}
