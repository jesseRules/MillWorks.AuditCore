using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Audit Integrity Data Transfer Object
/// </summary>
public sealed class AuditIntegrityDto
{
    /// <summary>
    /// Id
    /// </summary>
    [JsonPropertyName("event_id")]
    [DisplayName("Event Id")]
    public Guid? Id { get; set; }
    
     /// <summary>
    /// Event Id
    /// </summary>
    [JsonPropertyName("event_id")]
    [DisplayName("Event Id")]
    public Guid EventId { get; set; }

    /// <summary>
    /// Inserted Date
    /// </summary>
    [JsonPropertyName("inserted_date")]
    [DisplayName("Inserted Date")]
    public DateTimeOffset? InsertedDate { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last Updated Date
    /// </summary>
    [JsonPropertyName("last_updated_date")]
    [DisplayName("Last Updated Date")]
    public DateTimeOffset? LastUpdatedDate { get; set; }

    /// <summary>
    /// Json Data - stores the complete audit event
    /// </summary>
    [JsonPropertyName("json_data")]
    [DisplayName("Json Data")]
    [MaxLength(4000)] // Optional: Limit the length for performance, adjust as needed
    public string? JsonData { get; set; }

    /// <summary>
    /// Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    [DisplayName("Event Type")]
    [MaxLength(256)]
    public string? EventType { get; set; }

    /// <summary>
    /// User - Email or Username for display
    /// </summary>
    [JsonPropertyName("user")]
    [DisplayName("User")]
    [MaxLength(256)]
    public string? User { get; set; }

    /// <summary>
    /// User Environment Name
    /// </summary>
    [JsonPropertyName("user_env_name")]
    [DisplayName("User Environment Name")]
    [MaxLength(256)]
    public string? UserEnvName { get; set; }

    /// <summary>
    /// User Id - AppUserDetailEntity.Id
    /// </summary>
    [JsonPropertyName("app_user_id")]
    [DisplayName("App User Id")]
    public Guid? UserId { get; set; }

    /// <summary>
    /// AspNet User Id - ApplicationUser.Id
    /// </summary>
    [Column("AspNetUserId")]
    [JsonPropertyName("asp_net_user_id")]
    [DisplayName("AspNet User Id")]
    public string? AspNetUserId { get; set; }

    /// <summary>
    /// User Full Name from AppUserDetailEntity
    /// </summary>
    [MaxLength(200)]
    [JsonPropertyName("user_full_name")]
    [DisplayName("User Full Name")]
    public string? UserFullName { get; set; }

    /// <summary>
    /// Start Date
    /// </summary>
    [JsonPropertyName("start_date")]
    [DisplayName("Start Date")]
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// End Date
    /// </summary>
    [JsonPropertyName("end_date")]
    [DisplayName("End Date")]
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// Duration in milliseconds
    /// </summary>
    [JsonPropertyName("duration")]
    [DisplayName("Duration")]
    public int? Duration { get; set; }

    /// <summary>
    /// Entity Type
    /// </summary>
    [MaxLength(100)]
    [JsonPropertyName("entity_type")]
    [DisplayName("Entity Type")]
    public string? EntityType { get; set; }

    /// <summary>
    /// Entity Id
    /// </summary>
    [MaxLength(450)]
    [JsonPropertyName("entity_id")]
    [DisplayName("Entity Id")]
    public string? EntityId { get; set; }

    /// <summary>
    /// Action performed on the entity
    /// </summary>
    [MaxLength(50)]
    [JsonPropertyName("action")]
    [DisplayName("Action")]
    public string? Action { get; set; }

    /// <summary>
    /// Additional Data
    /// </summary>
    [JsonPropertyName("additional_data")]
    [DisplayName("Additional Data")]
    [MaxLength(4000)]
    public string? AdditionalData { get; set; }

    /// <summary>
    /// Environment where the event occurred (e.g., Development, Staging, Production)
    /// </summary>
    [MaxLength(100)]
    [JsonPropertyName("environment")]
    [DisplayName("Environment")]
    public string? Environment { get; set; }

    /// <summary>
    /// Machine Name
    /// </summary>
    [MaxLength(200)]
    [JsonPropertyName("machine_name")]
    [DisplayName("Machine Name")]
    public string? MachineName { get; set; }

    /// <summary>
    /// Calling Method Name
    /// </summary>
    [MaxLength(200)]
    [JsonPropertyName("calling_method_name")]
    [DisplayName("Calling Method Name")]
    public string? CallingMethodName { get; set; }

    /// <summary>
    /// Assembly Name
    /// </summary>
    [MaxLength(200)]
    [JsonPropertyName("assembly_name")]
    [DisplayName("Assembly Name")]
    public string? AssemblyName { get; set; }

    /// <summary>
    /// Correlation Id
    /// </summary>
    [MaxLength(36)]
    [JsonPropertyName("correlation_id")]
    [DisplayName("Correlation Id")]
    public string? CorrelationId { get; set; }

    /// <summary>
    /// IP Address
    /// </summary>
    [MaxLength(45)]
    [JsonPropertyName("ip_address")]
    [DisplayName("IP Address")]
    public string? IpAddress { get; set; }

    /// <summary>
    /// User Agent
    /// </summary>
    [MaxLength(500)]
    [JsonPropertyName("user_agent")]
    [DisplayName("User Agent")]
    public string? UserAgent { get; set; }

    /// <summary>
    /// Request Path
    /// </summary>
    [MaxLength(500)]
    [JsonPropertyName("request_path")]
    [DisplayName("Request Path")]
    public string? RequestPath { get; set; }

    /// <summary>
    /// Request Method
    /// </summary>
    [MaxLength(10)]
    [JsonPropertyName("request_method")]
    [DisplayName("Request Method")]
    public string? RequestMethod { get; set; }

    /// <summary>
    /// Tenant Id
    /// </summary>
    [JsonPropertyName("tenant_id")]
    [DisplayName("Tenant Id")]
    public Guid? TenantId { get; set; }
    
    /// <summary>
    /// SHA-256 hash of the audit event data (Base64-encoded)
    /// </summary>
    [MaxLength(44)]
    [JsonPropertyName("event_hash")]
    [DisplayName("Event Hash")]
    public string? EventHash { get; set; }

    /// <summary>
    /// Hash of the previous audit event (blockchain-style chaining)
    /// </summary>
    [MaxLength(44)]
    [JsonPropertyName("previous_event_hash")]
    [DisplayName("Previous Event Hash")]
    public string? PreviousEventHash { get; set; }

    /// <summary>
    /// Digital signature of the event hash (if using PKI)
    /// </summary>
    [MaxLength(512)]
    [JsonPropertyName("digital_signature")]
    [DisplayName("Digital Signature")]
    public string? DigitalSignature { get; set; }

    /// <summary>
    /// Timestamp from a trusted time source
    /// </summary>
    [JsonPropertyName("trusted_timestamp")]
    [DisplayName("Trusted Timestamp")]
    public DateTimeOffset? TrustedTimestamp { get; set; }

    /// <summary>
    /// Sequence number for ordering verification
    /// </summary>
    [JsonPropertyName("sequence_number")]
    [DisplayName("Sequence Number")]
    public long? SequenceNumber { get; set; }

    /// <summary>
    /// HMAC for additional integrity verification (Base64-encoded)
    /// </summary>
    [MaxLength(44)]
    [JsonPropertyName("hmac_signature")]
    [DisplayName("HMAC Signature")]
    public string? HmacSignature { get; set; }

    /// <summary>
    /// Checksum of critical fields
    /// </summary>
    [MaxLength(44)]
    [JsonPropertyName("checksum")]
    [DisplayName("Checksum")]
    public string? Checksum { get; set; }

    /// <summary>
    /// Version of the integrity algorithm used
    /// </summary>
    [JsonPropertyName("algorithm_version")]
    [DisplayName("Algorithm Version")]
    public int? AlgorithmVersion { get; set; }

    /// <summary>
    /// Parameters for additional configuration or metadata
    /// </summary>
    [MaxLength(2000)]
    [JsonPropertyName("parameters")]
    [DisplayName("Parameters")]
    public string? Parameters { get; set; }

    /// <summary>
    /// Audit Event DTO
    /// </summary>
    [JsonPropertyName("audit_event")]
    [DisplayName("Audit Event")]
    public AuditEventDto? AuditEvent { get; set; }

}
