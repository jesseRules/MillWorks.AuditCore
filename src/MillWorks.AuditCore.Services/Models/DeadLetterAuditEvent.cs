using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Dead Letter Event wrapper with metadata
/// </summary>
public sealed class DeadLetterAuditEvent
{
    /// <summary>
    /// Identifier for the dead letter event
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Failure timestamp
    /// </summary>
    public DateTimeOffset FailedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Failure reason if available
    /// </summary>
    public string? FailureReason { get; set; }

    /// <summary>
    /// Exception type name (e.g. "DbUpdateException")
    /// </summary>
    public string? ExceptionType { get; set; }

    /// <summary>
    /// Truncated exception message. Full messages may contain sensitive data
    /// (SQL errors, connection strings, user identifiers); this is capped at 256 characters.
    /// </summary>
    public string? ExceptionMessage { get; set; }

    /// <summary>
    /// Exception stack trace. Only populated when IncludeStackTraces is explicitly
    /// enabled in ResilienceOptions. Null by default to avoid leaking internal
    /// implementation details, provider errors, and sensitive embedded values.
    /// </summary>
    public string? ExceptionStackTrace { get; set; }

    /// <summary>
    /// Retry count
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Last retry timestamp
    /// </summary>
    public DateTimeOffset? LastRetryAt { get; set; }

    /// <summary>
    /// Indicates whether the event has been successfully processed
    /// </summary>
    public bool IsProcessed { get; set; }

    /// <summary>
    /// Processed timestamp if the event has been successfully processed
    /// </summary>
    public DateTimeOffset? ProcessedAt { get; set; }

    /// <summary>
    /// Original audit event identifier
    /// </summary>
    public string OriginalEventId { get; set; } = string.Empty;

    /// <summary>
    /// Original audit event that failed processing
    /// </summary>
    public AuditEvent? OriginalEvent { get; set; }

    /// <summary>
    /// Original entity associated with the audit event, if applicable
    /// </summary>
    public AuditEventEntity? OriginalEntity { get; set; }

    /// <summary>
    /// Metadata associated with the dead letter event
    /// </summary>
    public Dictionary<string, object> Metadata { get; set; } = new();
}