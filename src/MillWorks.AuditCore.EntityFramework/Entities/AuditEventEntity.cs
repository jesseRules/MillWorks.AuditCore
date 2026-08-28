using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Audit Event Entity
/// </summary>
[Table("AuditEvents")]
[Index(nameof(UserId), Name = "IX_AuditEvents_UserId")]
[Index(nameof(AspNetUserId), Name = "IX_AuditEvents_AspNetUserId")]
[Index(nameof(TenantId), Name = "IX_AuditEvents_TenantId")]
[Index(nameof(EventType), Name = "IX_AuditEvents_EventType")]
[Index(nameof(EntityType), nameof(EntityId), Name = "IX_AuditEvents_Entity")]
[Index(nameof(CorrelationId), Name = "IX_AuditEvents_CorrelationId")]
[Index(nameof(InsertedDate), nameof(EventType), Name = "IX_AuditEvents_Date_Type")]
[Index(nameof(IntegrityStatus), Name = "IX_AuditEvents_IntegrityStatus")]
public class AuditEventEntity : AuditAggregateRoot
{
    /// <summary>Maximum persisted user-agent length.</summary>
    public const int MaxUserAgentLength = 500;

    /// <summary>Maximum persisted request-path length.</summary>
    public const int MaxRequestPathLength = 2048;

    /// <summary>
    /// Event Id
    /// </summary>
    [Key]
    [Column("EventId")]
    [JsonPropertyName("event_id")]
    [DisplayName("Event Id")]
    public Guid EventId { get; set; } = Guid.NewGuid();
    
    /// <summary>
    /// Entity Id
    /// </summary>
    [JsonPropertyName("entity_id")]
    [DisplayName("Entity Id")]
    [MaxLength(256)]
    public string? EntityId { get; set; }

    /// <summary>
    /// Inserted Date
    /// </summary>
    [Column("InsertedDate")]
    [JsonPropertyName("inserted_date")]
    [DisplayName("Inserted Date")]
    public DateTimeOffset? InsertedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last Updated Date
    /// </summary>
    [Column("LastUpdatedDate")]
    [JsonPropertyName("last_updated_date")]
    [DisplayName("Last Updated Date")]
    public DateTimeOffset? LastUpdatedDate { get; set; }

    /// <summary>
    /// Json Data - stores the complete audit event
    /// </summary>
    [Column("JsonData")]
    [JsonPropertyName("json_data")]
    [DisplayName("Json Data")]
    public string? JsonData { get; set; }

    /// <summary>
    /// Event Type
    /// </summary>
    [Column("EventType")]
    [JsonPropertyName("event_type")]
    [DisplayName("Event Type")]
    [MaxLength(256)]
    public string? EventType { get; set; }

    /// <summary>
    /// User - Email or Username for display
    /// </summary>
    [Column("User")]
    [JsonPropertyName("user")]
    [DisplayName("User")]
    [MaxLength(256)]
    public string? User { get; set; }

    /// <summary>
    /// User Environment Name
    /// </summary>
    [Column("UserEnvName")]
    [JsonPropertyName("user_env_name")]
    [DisplayName("User Environment Name")]
    [MaxLength(256)]
    public string? UserEnvName { get; set; }

    /// <summary>
    /// User Id - AppUserDetailEntity.Id
    /// </summary>
    [Column("AppUserId")]
    [JsonPropertyName("app_user_id")]
    [DisplayName("App User Id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// AspNet User Id - ApplicationUser.Id
    /// </summary>
    [MaxLength(450)]
    [Column("AspNetUserId")]
    [JsonPropertyName("asp_net_user_id")]
    [DisplayName("AspNet User Id")]
    public string? AspNetUserId { get; set; }

    /// <summary>
    /// User Full Name from AppUserDetailEntity
    /// </summary>
    [MaxLength(200)]
    [Column("UserFullName")]
    [JsonPropertyName("user_full_name")]
    [DisplayName("User Full Name")]
    public string? UserFullName { get; set; }

    /// <summary>
    /// Start Date
    /// </summary>
    [Column("StartDate")]
    [JsonPropertyName("start_date")]
    [DisplayName("Start Date")]
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// End Date
    /// </summary>
    [Column("EndDate")]
    [JsonPropertyName("end_date")]
    [DisplayName("End Date")]
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Duration in milliseconds
    /// </summary>
    [Column("Duration")]
    [JsonPropertyName("duration")]
    [DisplayName("Duration")]
    public int? Duration { get; set; }

    /// <summary>
    /// Entity Type
    /// </summary>
    [MaxLength(100)]
    [Column("EntityType")]
    [JsonPropertyName("entity_type")]
    [DisplayName("Entity Type")]
    public string? EntityType { get; set; }

    /// <summary>
    /// Action performed on the entity
    /// </summary>
    [MaxLength(50)]
    [Column("Action")]
    [JsonPropertyName("action")]
    [DisplayName("Action")]
    public string? Action { get; set; }

    /// <summary>
    /// Additional Data
    /// </summary>
    [Column("AdditionalData")]
    [JsonPropertyName("additional_data")]
    [DisplayName("Additional Data")]
    public string? AdditionalData { get; set; }

    /// <summary>
    /// Environment where the event occurred (e.g., Development, Staging, Production)
    /// </summary>
    [MaxLength(100)]
    [Column("Environment")]
    [JsonPropertyName("environment")]
    [DisplayName("Environment")]
    public string? Environment { get; set; }

    /// <summary>
    /// Machine Name
    /// </summary>
    [MaxLength(200)]
    [Column("MachineName")]
    [JsonPropertyName("machine_name")]
    [DisplayName("Machine Name")]
    public string? MachineName { get; set; }

    /// <summary>
    /// Calling Method Name
    /// </summary>
    [MaxLength(200)]
    [Column("CallingMethodName")]
    [JsonPropertyName("calling_method_name")]
    [DisplayName("Calling Method Name")]
    public string? CallingMethodName { get; set; }

    /// <summary>
    /// Assembly Name
    /// </summary>
    [MaxLength(200)]
    [Column("AssemblyName")]
    [JsonPropertyName("assembly_name")]
    [DisplayName("Assembly Name")]
    public string? AssemblyName { get; set; }

    /// <summary>
    /// Correlation Id. Supports W3C traceparent (55 chars) and other tracing formats up to 128 chars.
    /// </summary>
    [MaxLength(128)]
    [Column("CorrelationId")]
    [JsonPropertyName("correlation_id")]
    [DisplayName("Correlation Id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// IP Address
    /// </summary>
    [MaxLength(45)]
    [Column("IpAddress")]
    [JsonPropertyName("ip_address")]
    [DisplayName("IP Address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User Agent
    /// </summary>
    [MaxLength(MaxUserAgentLength)]
    [Column("UserAgent")]
    [JsonPropertyName("user_agent")]
    [DisplayName("User Agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request Path
    /// </summary>
    [MaxLength(MaxRequestPathLength)]
    [Column("RequestPath")]
    [JsonPropertyName("request_path")]
    [DisplayName("Request Path")]
    public string? RequestPath { get; set; }

    /// <summary>
    /// Request Method
    /// </summary>
    [MaxLength(10)]
    [Column("RequestMethod")]
    [JsonPropertyName("request_method")]
    [DisplayName("Request Method")]
    public string? RequestMethod { get; set; }

    /// <summary>
    /// Tenant Id
    /// </summary>
    [Column("TenantId")]
    [JsonPropertyName("tenant_id")]
    [DisplayName("Tenant Id")]
    public Guid? TenantId { get; set; }

    /// <summary>
    /// Tracks whether the integrity record (hash chain entry) for this event
    /// has been created. In strict mode this is set to <see cref="IntegrityStatus.Completed"/>
    /// at insert time. In batched mode it starts as <see cref="IntegrityStatus.Pending"/>
    /// and is updated after the background batcher processes it.
    /// </summary>
    [Column("IntegrityStatus")]
    [JsonPropertyName("integrity_status")]
    [DisplayName("Integrity Status")]
    public IntegrityStatus IntegrityStatus { get; set; } = IntegrityStatus.Pending;

    // ===== TAMPER EVIDENCE NAVIGATION PROPERTY =====
    
    /// <summary>
    /// Navigation property to the audit integrity record for tamper evidence
    /// This creates a one-to-one relationship with AuditIntegrityEntity
    /// </summary>
    [JsonIgnore]
    public virtual AuditIntegrityEntity? AuditIntegrity { get; set; }
}
