using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Response wrapper for chart data that includes truncation metadata.
/// </summary>
public sealed class AuditChartDataResponse
{
    /// <summary>
    /// The chart data points.
    /// </summary>
    [JsonPropertyName("items")]
    public List<AuditChartData> Items { get; set; } = new();

    /// <summary>
    /// Whether the results were truncated due to exceeding query limits.
    /// When true, <see cref="TruncatedAt"/> indicates the max rows that were returned.
    /// </summary>
    [JsonPropertyName("is_truncated")]
    public bool IsTruncated { get; set; }

    /// <summary>
    /// The maximum row count if truncation occurred.
    /// </summary>
    [JsonPropertyName("truncated_at")]
    public int? TruncatedAt { get; set; }
}
