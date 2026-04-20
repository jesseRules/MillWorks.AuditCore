using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Enhanced AuditLogEntity with better indexes
/// </summary>
[Table("AuditLogs")]
[Index(nameof(CreatedAt), Name = "IX_AuditLogs_CreatedAt")]
[Index(nameof(EntityName), nameof(EntityId), Name = "IX_AuditLogs_Entity")]
[Index(nameof(CreatedById), Name = "IX_AuditLogs_CreatedBy")]
[Index(nameof(Action), Name = "IX_AuditLogs_Action")]
[Index(nameof(CreatedAt), nameof(EntityName), Name = "IX_AuditLogs_Date_Entity")]
[Index(nameof(CorrelationId), Name = "IX_AuditLogs_CorrelationId")]
public sealed class AuditLogEntity: AppendOnlyEntity
{
    /// <summary>
    /// Entity name (type)
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Column("EntityType", TypeName = "nvarchar(100)")]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Entity ID (primary key of the entity).
    /// </summary>
    [Column("EntityId", TypeName = "uniqueidentifier")]
    public Guid? EntityId { get; set; }

    /// <summary>
    /// Action performed (Created, Updated, Deleted)
    /// </summary>
    [Required]
    [Column("Action", TypeName = "int")]
    public AuditAction Action { get; set; } = AuditAction.Created;

    /// <summary>
    /// Property name that was changed
    /// </summary>
    [MaxLength(200)]
    [Column("PropertyName", TypeName = "nvarchar(200)")]
    public string? PropertyName { get; set; }

    /// <summary>
    /// Old value of the property
    /// </summary>
    [Column("OldValue")]
    [MaxLength(4000)]
    public string? OldValue { get; set; }

    /// <summary>
    /// New value of the property
    /// </summary>
    [Column("NewValue")]
    [MaxLength(4000)]
    public string? NewValue { get; set; }

    /// <summary>
    /// Description of the audit log entry
    /// </summary>
    [MaxLength(500)]
    [Column("Description", TypeName = "nvarchar(500)")]
    public string? Description { get; set; }

    /// <summary>
    /// Additional data in JSON format
    /// </summary>
    [Column("AdditionalData")]
    [MaxLength(4000)]
    public string? AdditionalData { get; set; }

    /// <summary>
    /// Correlation ID linking this log entry to the originating request's AuditEvent.
    /// Matches AuditEventEntity.CorrelationId for cross-table joins.
    /// </summary>
    [MaxLength(36)]
    [Column("CorrelationId", TypeName = "nvarchar(36)")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// User agent string
    /// </summary>
    [MaxLength(500)]
    [Column("UserAgent", TypeName = "nvarchar(500)")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// IP address of the user
    /// </summary>
    [MaxLength(45)]
    [Column("IpAddress", TypeName = "varchar(45)")]
    public string? IpAddress { get; set; }
}