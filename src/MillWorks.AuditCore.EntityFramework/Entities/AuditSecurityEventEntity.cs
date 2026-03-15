using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Entity representing a security event detected within the audit system.
/// Security events are generated in response to suspicious activities, integrity violations,
/// unauthorized access attempts, and other security-related incidents.
/// </summary>
[Table("SecurityEvents", Schema = "audit")]
[Index(nameof(EventType), Name = "IX_SecurityEvents_EventType")]
[Index(nameof(Severity), Name = "IX_SecurityEvents_Severity")]
[Index(nameof(DetectedAt), Name = "IX_SecurityEvents_DetectedAt")]
[Index(nameof(Status), Name = "IX_SecurityEvents_Status")]
public class AuditSecurityEventEntity : AuditAggregateRoot
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
    /// Current status of the security event indicating its lifecycle state (e.g., "Open", "Investigating", "Resolved").
    /// </summary>
    [Required]
    [Column("Status")]
    public SecurityEventStatus Status { get; set; }

    /// <summary>
    /// Timestamp indicating when the security event was resolved, if applicable.
    /// </summary>
    [Column("ResolvedAt")]
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    /// Identifier or name of the user or system that resolved the security event.
    /// </summary>
    [MaxLength(256)]
    [Column("ResolvedBy")]
    public string? ResolvedBy { get; set; }

    /// <summary>
    /// Detailed description of the actions taken to resolve the security event and any findings from the investigation.
    /// </summary>
    [MaxLength(1000)]
    [Column("Resolution")]
    public string? Resolution { get; set; }

    /// <summary>
    /// Navigation property to the related audit event that triggered or is associated with this security event.
    /// </summary>
    [ForeignKey(nameof(RelatedAuditEventId))]
    public virtual AuditEventEntity? RelatedAuditEvent { get; set; }
}