using System.ComponentModel.DataAnnotations;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>Immutable comparison evidence, committed with the corresponding accepted write.
/// Channels separate event, entity-envelope and consumer-outbox identity domains; IDs within
/// each channel are global across tenants. Payload cleanup must not implicitly remove this evidence.</summary>
public sealed class AuditEnvelopeIdentity : IAppendOnlyEntity
{
    [MaxLength(16)] public string Channel { get; set; } = string.Empty;
    public Guid LogicalId { get; set; }
    public AuditEnvelopeKind Kind { get; set; }
    public Guid? TenantId { get; set; }
    public int ContentVersion { get; set; }
    [MaxLength(64)] public string Fingerprint { get; set; } = string.Empty;
    public DateTimeOffset AcceptedAt { get; set; }
}
