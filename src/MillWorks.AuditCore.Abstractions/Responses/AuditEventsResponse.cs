using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
///     Audit Events Response
/// </summary>
public sealed class AuditEventsResponse
{
    /// <summary>
    ///     Total Items
    /// </summary>
    [JsonPropertyName("total_items")]
    [Display(Name = "Total Items")]
    public int TotalItems { get; set; }

    /// <summary>
    ///     User Types
    /// </summary>
    [JsonPropertyName("items")]
    [Display(Name = "Items")]
    public List<AuditEventDto>? Items { get; set; } = [];

    /// <summary>
    ///     Offset
    /// </summary>
    [JsonPropertyName("offset")]
    [Display(Name = "Offset")]
    public int Offset { get; set; }

    /// <summary>
    ///     Limit
    /// </summary>
    [JsonPropertyName("limit")]
    [Display(Name = "Limit")]
    public int Limit { get; set; }

    /// <summary>
    ///     TotalPages
    /// </summary>
    [JsonPropertyName("total_pages")]
    [Display(Name = "Total Pages")]
    public int TotalPages { get; set; }

    /// <summary>
    ///     Current Page
    /// </summary>
    [JsonPropertyName("current_page")]
    [Display(Name = "Current Page")]
    public int CurrentPage { get; set; }
}