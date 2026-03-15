using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// DTO containing information about the target object or resource affected by an audit event.
/// </summary>
public sealed class AuditTarget
{
    /// <summary>
    /// Type or class name of the target object (e.g., "User", "Document", "Configuration").
    /// </summary>
    [MaxLength(100)]
    [Display(Name = "Type")]
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>
    /// Previous state or value of the target object before the change.
    /// </summary>
    [Display(Name = "Old Value")]
    [JsonPropertyName("old")]
    public object? Old { get; init; }

    /// <summary>
    /// New state or value of the target object after the change.
    /// </summary>
    [Display(Name = "New Value")]
    [JsonPropertyName("new")]
    public object? New { get; init; }
}