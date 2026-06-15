using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// Represents a custom audit event for comprehensive system activity tracking.
/// </summary>
public sealed class AuditEvent
{
    /// <summary>
    /// Unique identifier for this audit event instance.
    /// </summary>
    [Required]
    [Display(Name = "Event ID")]
    [JsonPropertyName("event_id")]
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Type of event being audited (e.g., "User.Login", "Data.Update", "Security.AccessDenied").
    /// </summary>
    [Required]
    [MaxLength(100)]
    [Display(Name = "Event Type")]
    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// UTC timestamp when the audited event started or occurred.
    /// </summary>
    [Required]
    [Display(Name = "Start Date")]
    [JsonPropertyName("start_date")]
    public DateTimeOffset StartDate { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// UTC timestamp when the audited event ended (optional for instantaneous events).
    /// </summary>
    [Display(Name = "End Date")]
    [JsonPropertyName("end_date")]
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Duration of the event in milliseconds (calculated automatically when EndDate is set).
    /// </summary>
    [Range(0, int.MaxValue)]
    [Display(Name = "Duration (ms)")]
    [JsonPropertyName("duration")]
    [JsonInclude]
    public int? Duration { get; private set; }

    /// <summary>
    /// Environment and context information for where this event occurred.
    /// </summary>
    [Required]
    [Display(Name = "Environment")]
    [JsonPropertyName("environment")]
    public AuditEnvironment Environment { get; set; } = new();

    /// <summary>
    /// Information about the target object or resource that was affected by this event.
    /// </summary>
    [Display(Name = "Target")]
    [JsonPropertyName("target")]
    public AuditTarget? Target { get; set; }

    /// <summary>
    /// Custom fields for storing additional event-specific data and metadata.
    /// </summary>
    [Display(Name = "Custom Fields")]
    [JsonPropertyName("custom_fields")]
    public Dictionary<string, object?> CustomFields { get; set; } = new();

    /// <summary>
    /// Indicates whether the audited operation completed successfully.
    /// </summary>
    [Display(Name = "Success")]
    [JsonPropertyName("success")]
    public bool? Success { get; set; }

    /// <summary>
    /// Error message describing why the operation failed, if applicable.
    /// </summary>
    [MaxLength(1000)]
    [Display(Name = "Error Message")]
    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// System fields for internal tracking (e.g., database IDs, versioning).
    /// </summary>
    [Display(Name = "System Fields")]
    [JsonPropertyName("system_fields")]
    public Dictionary<string, object?> SystemFields { get; set; } = new();

    /// <summary>
    /// Correlation ID to link related events across systems.
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Correlation ID")]
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// Parent event ID for hierarchical event tracking.
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Parent ID")]
    [JsonPropertyName("parent_id")]
    public string? ParentId { get; set; }

    /// <summary>
    /// Session ID from the client making the request.
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Session ID")]
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>
    /// IP address of the client making the request.
    /// </summary>
    [MaxLength(45)]
    [Display(Name = "IP Address")]
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User agent string from the client making the request.
    /// </summary>
    [MaxLength(512)]
    [Display(Name = "User Agent")]
    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request ID from the web request, if applicable.
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Request ID")]
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    /// <summary>
    /// Name of the entity being audited.
    /// </summary>
    [MaxLength(255)]
    [Display(Name = "Entity Name")]
    [JsonPropertyName("entity_name")]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Audit action performed (Created, Updated, Deleted, etc.).
    /// </summary>
    [Display(Name = "Action")]
    [JsonPropertyName("action")]
    public AuditAction Action { get; set; } = AuditAction.Unknown;

    /// <summary>
    /// User ID from the application's user system.
    /// </summary>
    [Display(Name = "User ID")]
    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// ASP.NET Identity User ID.
    /// </summary>
    [MaxLength(450)]
    [Display(Name = "ASP.NET User ID")]
    [JsonPropertyName("aspnet_user_id")]
    public string? AspNetUserId { get; set; }

    /// <summary>
    /// Primary key values of the entity.
    /// </summary>
    [Display(Name = "Key Values")]
    [JsonPropertyName("key_values")]
    public Dictionary<string, object?> KeyValues { get; set; } = new();

    /// <summary>
    /// Original values before the change.
    /// </summary>
    [Display(Name = "Old Values")]
    [JsonPropertyName("old_values")]
    public Dictionary<string, object?> OldValues { get; set; } = new();

    /// <summary>
    /// New values after the change.
    /// </summary>
    [Display(Name = "New Values")]
    [JsonPropertyName("new_values")]
    public Dictionary<string, object?> NewValues { get; set; } = new();

    /// <summary>
    /// List of properties that were changed.
    /// </summary>
    [Display(Name = "Changed Properties")]
    [JsonPropertyName("changed_properties")]
    public List<string> ChangedProperties { get; set; } = new();

    /// <summary>
    /// Email address of the user who performed the action.
    /// </summary>
    [MaxLength(256)]
    [Display(Name = "User Email")]
    [JsonPropertyName("user_email")]
    public string? UserEmail { get; set; }

    /// <summary>
    /// Calculates and sets the Duration property based on StartDate and EndDate.
    /// Clamps to [0, int.MaxValue] to satisfy the Range attribute and handle EndDate &lt; StartDate.
    /// </summary>
    public void CalculateDuration()
    {
        if (EndDate.HasValue)
        {
            long ms = (long)(EndDate.Value - StartDate).TotalMilliseconds;
            Duration = (int)Math.Clamp(ms, 0, int.MaxValue);
        }
    }
}
