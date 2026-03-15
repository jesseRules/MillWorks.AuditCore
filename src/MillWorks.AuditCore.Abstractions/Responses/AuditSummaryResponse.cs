using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Audit Summary Response
/// </summary>
public sealed class AuditSummaryResponse
{
    /// <summary>
    /// Total Events
    /// </summary>
    [JsonPropertyName("total_events")]
    [Display(Name = "Total")]
    public int TotalEvents { get; set; }

    /// <summary>
    /// Unique Users
    /// </summary>
    [JsonPropertyName("unique_users")]
    [Display(Name = "Unique")]
    public int UniqueUsers { get; set; }

    /// <summary>
    /// Event Types
    /// </summary>
    [JsonPropertyName("event_types")]
    [Display(Name = "Events")]
    public List<AuditEventTypeCount> EventTypes { get; set; } = new();

    /// <summary>
    /// Top Users
    /// </summary>
    [JsonPropertyName("top_users")]
    [Display(Name = "Top")]
    public List<AuditUserCount> TopUsers { get; set; } = new();

    /// <summary>
    /// Start Date
    /// </summary>
    [JsonPropertyName("start_date")]
    [Display(Name = "Start")]
    public DateTimeOffset? StartDate { get; set; }

    /// <summary>
    /// End Date
    /// </summary>
    [JsonPropertyName("end_date")]
    [Display(Name = "End")]
    public DateTimeOffset? EndDate { get; set; }
}