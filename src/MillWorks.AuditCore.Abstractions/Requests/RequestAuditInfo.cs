using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Requests;

/// <summary>
/// Contains detailed information about an HTTP request for auditing and logging purposes.
/// </summary>
public class RequestAuditInfo
{
    /// <summary>
    /// A unique identifier for the HTTP request, often from a correlation ID.
    /// </summary>
    [Display(Name = "RequestId")]
    [JsonPropertyName("request_id")]
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// The IP address of the client that made the request.
    /// </summary>
    [Display(Name = "IpAddress")]
    [JsonPropertyName("ip_address")]
    public string IpAddress { get; set; } = string.Empty;

    /// <summary>
    /// The User-Agent string from the request headers.
    /// </summary>
    [Display(Name = "UserAgent")]
    [JsonPropertyName("user_agent")]
    public string UserAgent { get; set; } = string.Empty;

    /// <summary>
    /// The unique identifier of the authenticated user, if available.
    /// </summary>
    [Display(Name = "UserId")]
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    /// <summary>
    /// The username of the authenticated user, if available.
    /// </summary>
    [Display(Name = "UserName")]
    [JsonPropertyName("user_name")]
    public string? UserName { get; set; }

    /// <summary>
    /// The path of the request URL (e.g., "/api/users").
    /// </summary>
    [Display(Name = "Path")]
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    /// <summary>
    /// The HTTP method of the request (e.g., "GET", "POST").
    /// </summary>
    [Display(Name = "Method")]
    [JsonPropertyName("method")]
    public string Method { get; set; } = string.Empty;

    /// <summary>
    /// The query string part of the request URL.
    /// </summary>
    [Display(Name = "QueryString")]
    [JsonPropertyName("query_string")]
    public string QueryString { get; set; } = string.Empty;

    /// <summary>
    /// The UTC timestamp when the request processing started.
    /// </summary>
    [Display(Name = "StartTime")]
    [JsonPropertyName("start_time")]
    public DateTimeOffset StartTime { get; set; }

    /// <summary>
    /// The UTC timestamp when the request processing ended.
    /// </summary>
    [Display(Name = "EndTime")]
    [JsonPropertyName("end_time")]
    public DateTimeOffset EndTime { get; set; }

    /// <summary>
    /// The total duration of the request processing.
    /// </summary>
    [Display(Name = "Duration")]
    [JsonPropertyName("duration")]
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// The HTTP status code of the response.
    /// </summary>
    [Display(Name = "StatusCode")]
    [JsonPropertyName("status_code")]
    public int StatusCode { get; set; }

    /// <summary>
    /// Indicates whether the request was made by an authenticated user.
    /// </summary>
    [Display(Name = "IsAuthenticated")]
    [JsonPropertyName("is_authenticated")]
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// The exception that occurred during request processing, if any.
    /// This property is ignored during JSON serialization to avoid circular references and excessive data.
    /// </summary>
    [JsonIgnore]
    public Exception? Exception { get; set; }
}