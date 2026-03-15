using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Audit Event Type Count
/// </summary>
public sealed class AuditUserCount
{
    /// <summary>
    /// User
    /// </summary>
    [JsonPropertyName("user")]
    [Display(Name = "User")]
    public string User { get; set; } = string.Empty;

    /// <summary>
    /// Count
    /// </summary>
    [JsonPropertyName("count")]
    [Display(Name = "Count")]
    public int Count { get; set; }
}