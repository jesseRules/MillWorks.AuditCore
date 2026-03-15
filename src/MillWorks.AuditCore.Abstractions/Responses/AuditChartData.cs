using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Audit Chart Data
/// </summary>
public sealed class AuditChartData
{
    /// <summary>
    /// Label
    /// </summary>
    [JsonPropertyName("label")]
    [Display(Name = "Label")]
    public string Label { get; set; } = string.Empty;

    /// <summary>
    /// Count
    /// </summary>
    [JsonPropertyName("count")]
    [Display(Name = "Count")]
    public int Count { get; set; }

    /// <summary>
    /// Date
    /// </summary>
    [JsonPropertyName("date")]
    [Display(Name = "Date")]
    public DateTimeOffset Date { get; set; }

    /// <summary>
    /// User
    /// </summary>
    [JsonPropertyName("user")]
    [Display(Name = "User")]
    public string? User { get; set; }

    /// <summary>
    /// Event Type
    /// </summary>
    [JsonPropertyName("event_type")]
    [Display(Name = "Event type")]
    public string? EventType { get; set; }
}