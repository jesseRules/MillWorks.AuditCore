using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Responses;

/// <summary>
/// Response wrapper for generated audit reports that includes truncation metadata.
/// </summary>
public sealed class AuditReportResponse
{
    /// <summary>
    /// The report content as a byte array.
    /// </summary>
    [JsonPropertyName("content")]
    public byte[] Content { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// The format of the report (e.g., "json", "csv").
    /// </summary>
    [JsonPropertyName("format")]
    public string Format { get; set; } = string.Empty;

    /// <summary>
    /// Whether the results were truncated due to exceeding export limits.
    /// When true, <see cref="TruncatedAt"/> indicates the max rows that were exported.
    /// </summary>
    [JsonPropertyName("is_truncated")]
    public bool IsTruncated { get; set; }

    /// <summary>
    /// The maximum row count if truncation occurred.
    /// </summary>
    [JsonPropertyName("truncated_at")]
    public int? TruncatedAt { get; set; }

    /// <summary>
    /// Total number of matching records in the date range (before truncation).
    /// </summary>
    [JsonPropertyName("total_records")]
    public int? TotalRecords { get; set; }
}
