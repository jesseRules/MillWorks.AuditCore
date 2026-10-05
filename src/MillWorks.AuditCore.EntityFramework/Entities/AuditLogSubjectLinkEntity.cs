using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>Normalized, indexed subject locator for an entity-change audit row.</summary>
[Table("AuditLogSubjectLinks")]
[PrimaryKey(nameof(AuditLogId), nameof(TenantId), nameof(SubjectId), nameof(Relationship))]
[Index(nameof(TenantId), nameof(SubjectId), nameof(OccurredAt), nameof(AuditLogId),
    Name = "IX_AuditLogSubjectLinks_Subject_Time")]
public sealed class AuditLogSubjectLinkEntity : IAppendOnlyEntity
{
    /// <summary>Owning audit-log row.</summary>
    public Guid AuditLogId { get; set; }

    /// <summary>Tenant that owns the subject-linked evidence.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Canonical host subject identifier.</summary>
    public Guid SubjectId { get; set; }

    /// <summary>Typed relationship, normally the source EF property name.</summary>
    [Required, MaxLength(128)]
    public string Relationship { get; set; } = string.Empty;

    /// <summary>Timestamp copied from the audit row for indexed keyset lookup.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Owning audit-log row.</summary>
    public AuditLogEntity AuditLog { get; set; } = null!;
}
