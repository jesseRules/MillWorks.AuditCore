using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// DTO representing a security event detected within the audit system.
/// </summary>
public sealed class SecurityEventDto
{
    /// <summary>
    /// Unique identifier for the security event.
    /// </summary>
    [Required]
    [Display(Name = "Event ID")]
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Type of security event that was detected (e.g., "AuditTamperAlert", "UnauthorizedAccess").
    /// </summary>
    [Required]
    [Display(Name = "Event Type")]
    [JsonPropertyName("event_type")]
    public SecurityEventType EventType { get; set; }

    /// <summary>
    /// Severity level of the security event.
    /// </summary>
    [Required]
    [Display(Name = "Severity")]
    [JsonPropertyName("severity")]
    public SecurityEventSeverity Severity { get; set; }

    /// <summary>
    /// Optional identifier of the related audit event that triggered this security event.
    /// </summary>
    [Display(Name = "Related Audit Event ID")]
    [JsonPropertyName("related_audit_event_id")]
    public Guid? RelatedAuditEventId { get; set; }

    /// <summary>
    /// Descriptive message providing details about the security event.
    /// Limit matches entity storage constraint.
    /// </summary>
    [Required]
    [MaxLength(500)]
    [Display(Name = "Message")]
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Dictionary containing additional context-specific details about the security event.
    /// </summary>
    [Display(Name = "Details")]
    [JsonPropertyName("details")]
    public Dictionary<string, object?> Details { get; set; } = new();

    /// <summary>
    /// Timestamp when the security event was detected.
    /// </summary>
    [Required]
    [Display(Name = "Detected At")]
    [JsonPropertyName("detected_at")]
    public DateTimeOffset DetectedAt { get; set; }

    /// <summary>
    /// Identifier or name of the entity that detected the security event.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Detected By")]
    [JsonPropertyName("detected_by")]
    public string? DetectedBy { get; set; }

    /// <summary>
    /// IP address from which the security event originated.
    /// </summary>
    [MaxLength(45)]
    [Display(Name = "IP Address")]
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Correlation ID linking related security events across a break-glass or investigation flow.
    /// </summary>
    [MaxLength(36)]
    [Display(Name = "Correlation ID")]
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Tenant identifier for multi-tenant deployments.
    /// </summary>
    [Display(Name = "Tenant ID")]
    [JsonPropertyName("tenant_id")]
    public Guid? TenantId { get; set; }

    /// <summary>
    /// User ID of the actor initiating the security event.
    /// </summary>
    [Display(Name = "Actor User ID")]
    [JsonPropertyName("actor_user_id")]
    public Guid? ActorUserId { get; set; }

    /// <summary>
    /// User ID of the subject affected by the security event (e.g., the user being accessed via break-glass).
    /// </summary>
    [Display(Name = "Subject User ID")]
    [JsonPropertyName("subject_user_id")]
    public Guid? SubjectUserId { get; set; }

    /// <summary>
    /// SHA-256 hash of the source IP address for privacy-preserving logging.
    /// </summary>
    [MaxLength(64)]
    [Display(Name = "Source IP Hash")]
    [JsonPropertyName("source_ip_hash")]
    public string? SourceIpHash { get; set; }

    /// <summary>
    /// SHA-256 hash of the user agent string for privacy-preserving logging.
    /// </summary>
    [MaxLength(64)]
    [Display(Name = "User Agent Hash")]
    [JsonPropertyName("user_agent_hash")]
    public string? UserAgentHash { get; set; }

    /// <summary>
    /// Operation name or identifier for the security event (e.g., "NetworkPolicyOverride", "MfaBypass").
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Operation")]
    [JsonPropertyName("operation")]
    public string? Operation { get; set; }

    /// <summary>
    /// Current status of the security event (e.g., "Open", "Investigating", "Resolved").
    /// </summary>
    [Required]
    [Display(Name = "Status")]
    [JsonPropertyName("status")]
    public SecurityEventStatus Status { get; set; }
}

/// <summary>
/// Enum representing the types of security events that can be detected in the audit system.
/// </summary>
public enum SecurityEventType
{
    /// <summary>
    /// Alert triggered when audit logs or records show signs of tampering or unauthorized modification.
    /// </summary>
    AuditTamperAlert,

    /// <summary>
    /// Event indicating an attempt to access resources without proper authorization.
    /// </summary>
    UnauthorizedAccess,

    /// <summary>
    /// Event indicating suspicious activity that may require investigation.
    /// </summary>
    SuspiciousActivity,

    /// <summary>
    /// Event indicating a violation of data or system integrity.
    /// </summary>
    IntegrityViolation,

    /// <summary>
    /// Event indicating the audit chain of custody has been broken.
    /// </summary>
    ChainBroken,

    /// <summary>
    /// Event indicating a gap in the sequence of audit records.
    /// </summary>
    SequenceGap,

    /// <summary>
    /// Event indicating a violation of compliance requirements or policies.
    /// </summary>
    ComplianceViolation,

    /// <summary>
    /// Event indicating potential unauthorized exfiltration of sensitive data.
    /// </summary>
    DataExfiltration,

    /// <summary>
    /// Event indicating an attempt to escalate user privileges beyond authorized levels.
    /// </summary>
    PrivilegeEscalation,

    /// <summary>
    /// Break-glass access attempt initiated.
    /// </summary>
    BreakGlassAttempt,

    /// <summary>
    /// Break-glass access denied (policy or validation failure).
    /// </summary>
    BreakGlassDenied,

    /// <summary>
    /// Break-glass challenge issued to the requestor.
    /// </summary>
    BreakGlassChallengeIssued,

    /// <summary>
    /// Break-glass challenge failed (incorrect response, expired, etc.).
    /// </summary>
    BreakGlassChallengeFailed,

    /// <summary>
    /// Break-glass grant issued successfully.
    /// </summary>
    BreakGlassGranted,

    /// <summary>
    /// Break-glass grant consumed (used for access).
    /// </summary>
    BreakGlassConsumed,

    /// <summary>
    /// Break-glass grant expired without consumption.
    /// </summary>
    BreakGlassExpired,

    /// <summary>
    /// Break-glass grant revoked administratively.
    /// </summary>
    BreakGlassRevoked,

    /// <summary>
    /// Break-glass policy or configuration changed.
    /// </summary>
    BreakGlassPolicyChanged,

    /// <summary>
    /// Break-glass enrollment or recovery method changed.
    /// </summary>
    BreakGlassEnrollmentChanged
}

/// <summary>
/// Enum representing the severity levels of security events.
/// </summary>
public enum SecurityEventSeverity
{
    /// <summary>
    /// Low severity - minimal impact or risk.
    /// </summary>
    Low,

    /// <summary>
    /// Medium severity - moderate impact or risk requiring attention.
    /// </summary>
    Medium,

    /// <summary>
    /// High severity - significant impact or risk requiring prompt action.
    /// </summary>
    High,

    /// <summary>
    /// Critical severity - severe impact or risk requiring immediate action.
    /// </summary>
    Critical
}

/// <summary>
/// Enum representing the status states of a security event throughout its lifecycle.
/// </summary>
public enum SecurityEventStatus
{
    /// <summary>
    /// Event is newly detected and awaiting investigation.
    /// </summary>
    Open,

    /// <summary>
    /// Event is currently being investigated.
    /// </summary>
    Investigating,

    /// <summary>
    /// Event has been investigated and resolved.
    /// </summary>
    Resolved,

    /// <summary>
    /// Event was determined to be a false positive.
    /// </summary>
    FalsePositive
}