using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.EntityFramework.Dto;

/// <summary>
/// AuditLogDto represents a Data Transfer Object for an audit log record, capturing details about actions performed on entities in the system.
/// </summary>
public sealed class AuditLogDto
{
    /// <summary>
    /// Unique identifier for the audit log entry.
    /// </summary>
    [Display(Name = "Audit Log ID")]
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>
    /// Type of entity that was audited (e.g., "Ticket", "Project", "User", "Sprint").
    /// </summary>
    [Display(Name = "Entity Type")]
    [JsonPropertyName("entity_type")]
    public string EntityName { get; set; } = string.Empty;

    /// <summary>
    /// Unique identifier of the entity that was audited.
    /// </summary>
    [Display(Name = "Entity ID")]
    [JsonPropertyName("entity_id")]
    public Guid EntityId { get; set; }

    /// <summary>
    /// Type of action that was performed on the entity (e.g., Create, Update, Delete).
    /// </summary>
    [Display(Name = "Action")]
    [JsonPropertyName("action")]
    public AuditAction Action { get; set; }

    /// <summary>
    /// Name of the specific property that was changed during the action, if applicable.
    /// </summary>
    [Display(Name = "Property Name")]
    [JsonPropertyName("property_name")]
    public string? PropertyName { get; set; }

    /// <summary>
    /// Previous value of the property before the action was performed, if applicable.
    /// </summary>
    [Display(Name = "Old Value")]
    [JsonPropertyName("old_value")]
    public string? OldValue { get; set; }

    /// <summary>
    /// New value of the property after the action was performed, if applicable.
    /// </summary>
    [Display(Name = "New Value")]
    [JsonPropertyName("new_value")]
    public string? NewValue { get; set; }

    /// <summary>
    /// Additional context or description about the action performed on the entity.
    /// </summary>
    [Display(Name = "Description")]
    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>
    /// JSON string containing complex data related to the audit log, such as metadata or extra information about the action.
    /// </summary>
    [Display(Name = "Additional Data")]
    [JsonPropertyName("additional_data")]
    public string? AdditionalData { get; set; }

    /// <summary>
    /// Correlation ID linking this log entry to the originating request's AuditEvent.
    /// </summary>
    [Display(Name = "Correlation ID")]
    [JsonPropertyName("correlation_id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// User agent string of the client that performed the action.
    /// </summary>
    [Display(Name = "User Agent")]
    [JsonPropertyName("user_agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// IP address of the client that performed the action.
    /// </summary>
    [Display(Name = "IP Address")]
    [JsonPropertyName("ip_address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// Date and time when the audit log entry was created.
    /// </summary>
    [Display(Name = "Created At")]
    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Unique identifier of the user who performed the audited action.
    /// </summary>
    [Display(Name = "Created By ID")]
    [JsonPropertyName("created_by_id")]
    public Guid CreatedById { get; set; }
}