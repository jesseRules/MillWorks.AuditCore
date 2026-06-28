using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Entity representing a security event detected within the audit system.
/// Security events are generated in response to suspicious activities, integrity violations,
/// unauthorized access attempts, and other security-related incidents.
/// <para>
/// Append-only: a recorded security event is an immutable fact and is never updated or deleted
/// through the EF change tracker (enforced by <c>AppendOnlyInterceptor</c> via <see cref="IAppendOnlyEntity"/>).
/// Operational triage and alert resolution are owned by the application security layer
/// (MillWorks.Security), not AuditCore.
/// </para>
/// </summary>
[Table("SecurityEvents")]
[Index(nameof(EventType), Name = "IX_SecurityEvents_EventType")]
[Index(nameof(Severity), Name = "IX_SecurityEvents_Severity")]
[Index(nameof(DetectedAt), Name = "IX_SecurityEvents_DetectedAt")]
[Index(nameof(Status), Name = "IX_SecurityEvents_Status")]
[Index(nameof(TenantId), Name = "IX_SecurityEvents_TenantId")]
[Index(nameof(ActorUserId), Name = "IX_SecurityEvents_ActorUserId")]
[Index(nameof(SubjectUserId), Name = "IX_SecurityEvents_SubjectUserId")]
[Index(nameof(CorrelationId), Name = "IX_SecurityEvents_CorrelationId")]
[Index(nameof(Operation), Name = "IX_SecurityEvents_Operation")]
public class AuditSecurityEventEntity : AuditAggregateRoot, IAppendOnlyEntity
{
    /// <summary>
    /// Type of security event that was detected (e.g., "AuditTamperAlert", "UnauthorizedAccess").
    /// </summary>
    [Required]
    [Column("EventType")]
    public SecurityEventType EventType { get; set; }

    /// <summary>
    /// Severity level of the security event indicating the urgency and impact of the incident.
    /// </summary>
    [Required]
    [Column("Severity")]
    public SecurityEventSeverity Severity { get; set; }

    /// <summary>
    /// Optional foreign key reference to the audit event that triggered or is related to this security event.
    /// </summary>
    [Column("RelatedAuditEventId")]
    public Guid? RelatedAuditEventId { get; set; }

    /// <summary>
    /// Descriptive message providing initial details about the security event.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Column("Message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialized dictionary containing additional context-specific details about the security event.
    /// </summary>
    [Column("Details")]
    [MaxLength(4000)]
    public string? DetailsJson { get; set; }

    /// <summary>
    /// Timestamp indicating when the security event was detected by the system.
    /// </summary>
    [Required]
    [Column("DetectedAt")]
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>
    /// Identifier or name of the system component or service that detected the security event.
    /// </summary>
    [MaxLength(256)]
    [Column("DetectedBy")]
    public string? DetectedBy { get; set; }

    /// <summary>
    /// IP address from which the security event originated or was associated with.
    /// Supports both IPv4 and IPv6 addresses.
    /// </summary>
    [MaxLength(45)]
    [Column("IpAddress")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Correlation ID linking related security events across a break-glass or investigation flow.
    /// </summary>
    [MaxLength(36)]
    [Column("CorrelationId")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Tenant identifier for multi-tenant deployments.
    /// </summary>
    [Column("TenantId")]
    public Guid? TenantId { get; set; }

    /// <summary>
    /// User ID of the actor initiating the security event.
    /// </summary>
    [Column("ActorUserId")]
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// User ID of the subject affected by the security event (e.g., the user being accessed via break-glass).
    /// </summary>
    [Column("SubjectUserId")]
    public Guid? SubjectUserId { get; set; }

    /// <summary>
    /// SHA-256 hash of the source IP address for privacy-preserving logging.
    /// </summary>
    [MaxLength(64)]
    [Column("SourceIpHash")]
    public string? SourceIpHash { get; set; }

    /// <summary>
    /// SHA-256 hash of the user agent string for privacy-preserving logging.
    /// </summary>
    [MaxLength(64)]
    [Column("UserAgentHash")]
    public string? UserAgentHash { get; set; }

    /// <summary>
    /// Operation name or identifier for the security event (e.g., "NetworkPolicyOverride", "MfaBypass").
    /// </summary>
    [MaxLength(100)]
    [Column("Operation")]
    public string? Operation { get; set; }

    /// <summary>
    /// Current status of the security event indicating its lifecycle state (e.g., "Open", "Investigating", "Resolved").
    /// </summary>
    [Required]
    [Column("Status")]
    public SecurityEventStatus Status { get; set; }

    /// <summary>
    /// Navigation property to the related audit event that triggered or is associated with this security event.
    /// </summary>
    [ForeignKey(nameof(RelatedAuditEventId))]
    public virtual AuditEventEntity? RelatedAuditEvent { get; set; }
}