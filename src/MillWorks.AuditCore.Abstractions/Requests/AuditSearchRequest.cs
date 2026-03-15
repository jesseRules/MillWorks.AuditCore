using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Requests;

/// <summary>
/// Audit Search Request
/// </summary>
public sealed class AuditSearchRequest
{
    /// <summary>
    /// Start Date
    /// </summary>
    [JsonPropertyName("start_date")]
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// End Date
    /// </summary>
    [JsonPropertyName("end_date")]
    public DateTimeOffset? EndDate { get; set; }

    /// <summary>
    /// User
    /// </summary>
    [JsonPropertyName("user")]
    public string? User { get; set; }

    /// <summary>
    /// Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    public string? EventType { get; set; }
    
    /// <summary>
    /// Entity Type
    /// </summary>
    [JsonPropertyName("entity_type")]
    public string? EntityType { get; set; }

    /// <summary>
    /// Search Term
    /// </summary>
    [JsonPropertyName("search_term")]
    public string? SearchTerm { get; set; }

    /// <summary>
    /// Offset
    /// </summary>
    [JsonPropertyName("offset")]
    public int Offset { get; set; } 

    /// <summary>
    /// Limit
    /// </summary>
    [JsonPropertyName("limit")]
    public int Limit { get; set; } = 50;
}