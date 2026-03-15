using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
///     Audit
/// </summary>
public sealed class AuditEventDto
{
    /// <summary>
    ///     Event Id
    /// </summary>
    [JsonPropertyName("event_id")]
    [DisplayName("Event Id")]
    public Guid? EventId { get; set; }


    /// <summary>
    ///     Inserted Date
    /// </summary>
    [JsonPropertyName("inserted_date")]
    [DisplayName("Inserted Date")]
    public DateTimeOffset? InsertedDate { get; set; } = DateTimeOffset.Now;

    /// <summary>
    ///     Last Updated Date
    /// </summary>
    [JsonPropertyName("last_updated_date")]
    [DisplayName("Last Updated Date")]
    public DateTimeOffset? LastUpdatedDate { get; set; }

    /// <summary>
    ///     Json Data
    /// </summary>
    [JsonPropertyName("json_data")]
    [DisplayName("Json Data")]
    [MaxLength(int.MaxValue)]
    public string? JsonData { get; set; }


    /// <summary>
    ///     Data
    /// </summary>
    [JsonPropertyName("data")]
    [DisplayName("Data")]
    public AuditEventJsonDataParsedResponse? Data { get; set; }

    /// <summary>
    ///     Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    [DisplayName("Event Type")]
    [MaxLength(256)]
    public string? EventType { get; set; }

    /// <summary>
    ///     User
    /// </summary>
    [JsonPropertyName("user")]
    [DisplayName("User")]
    [MaxLength(256)]
    public string? User { get; set; }


    /// <summary>
    ///     User Environment Name
    /// </summary>
    [JsonPropertyName("user_env_name")]
    [DisplayName("User Environment Name")]
    [MaxLength(256)]
    public string? UserEnvName { get; set; }
    
    /// <summary>
    /// Navigation property to the audit integrity record for tamper evidence
    /// This creates a one-to-one relationship with AuditIntegrityEntity
    /// </summary>
    [JsonIgnore]
    public AuditIntegrityDto? AuditIntegrity { get; set; }

    /// <summary>
    /// Entity Id
    /// </summary>
    [JsonPropertyName("entity_id")]
    [DisplayName("Entity Id")]
    [MaxLength(256)]
    public string? EntityId { get; set; }
}