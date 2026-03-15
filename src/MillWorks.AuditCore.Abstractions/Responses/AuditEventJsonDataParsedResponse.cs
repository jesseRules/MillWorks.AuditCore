using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Audit Event Json Data Parsed - Updated without Audit.NET dependencies
/// </summary>
public sealed class AuditEventJsonDataParsedResponse
{
    /// <summary>
    /// Event ID
    /// </summary>
    [JsonPropertyName("event_id")]
    [DisplayName("Event ID")]
    public Guid? EventId { get; init; }

    /// <summary>
    /// Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    [DisplayName("Event Type")]
    public string? EventType { get; init; }

    /// <summary>
    /// Environment information
    /// </summary>
    [JsonPropertyName("environment")]
    [DisplayName("Environment")]
    public AuditEnvironmentResponse? Environment { get; set; }

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
    public DateTimeOffset? EndDate { get; init; }

    /// <summary>
    /// Duration in milliseconds
    /// </summary>
    [JsonPropertyName("duration")]
    [DisplayName("Duration")]
    public int? Duration { get; init; }

    /// <summary>
    /// Target object information
    /// </summary>
    [JsonPropertyName("target")]
    [DisplayName("Target")]
    public AuditTargetResponse? Target { get; set; }

    /// <summary>
    /// Custom fields with additional data
    /// </summary>
    [JsonPropertyName("custom_fields")]
    [DisplayName("Custom Fields")]
    public Dictionary<string, object?>? CustomFields { get; set; }

    /// <summary>
    /// Success status of the operation
    /// </summary>
    [JsonPropertyName("success")]
    [DisplayName("Success")]
    public bool? Success { get; init; }

    /// <summary>
    /// Error message if operation failed
    /// </summary>
    [JsonPropertyName("error_message")]
    [DisplayName("Error Message")]
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Entity-specific information for EF Core tracked changes
    /// </summary>
    [JsonPropertyName("entity_changes")]
    [DisplayName("Entity Changes")]
    public EntityChangesResponse? EntityChanges { get; set; }
}