namespace MillWorks.AuditCore.Services.Models;

/// <summary>
/// Stable request metadata captured as the target for HTTP request audit events.
/// </summary>
public sealed class RequestAuditTarget
{
    /// <summary>
    /// Request path.
    /// </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Whether the request included a query string.
    /// </summary>
    public bool HasQueryString { get; init; }

    /// <summary>
    /// User agent captured at request start.
    /// </summary>
    public string? UserAgent { get; init; }
}
