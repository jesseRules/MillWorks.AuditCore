using System.ComponentModel;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Represents the target of an audit event
/// </summary>
public sealed class AuditTargetResponse
{
    /// <summary>
    /// Type of the target object
    /// </summary>
    [JsonPropertyName("type")]
    [DisplayName("Type")]
    public string? Type { get; set; }

    /// <summary>
    /// Old values (for updates)
    /// </summary>
    [JsonPropertyName("old")]
    [DisplayName("Old Values")]
    public object? Old { get; set; }

    /// <summary>
    /// New values
    /// </summary>
    [JsonPropertyName("new")]
    [DisplayName("New Values")]
    public object? New { get; set; }
}