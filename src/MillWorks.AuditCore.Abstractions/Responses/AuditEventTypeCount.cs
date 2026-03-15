using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Audit Event Type Count
/// </summary>
public sealed class AuditEventTypeCount
{
    /// <summary>
    /// Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    [Display(Name = "Event type")]
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Count
    /// </summary>
    [JsonPropertyName("count")]
    [Display(Name = "Count")]
    public int Count { get; set; }
}