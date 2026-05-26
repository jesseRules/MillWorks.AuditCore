namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// Result of a batch audit logging operation.
/// </summary>
public sealed class BatchAuditResult
{
    public bool Success { get; init; }
    public int EventCount { get; init; }
    public IReadOnlyList<AuditEvent> FailedEvents { get; init; } = [];
    public Exception? Exception { get; init; }

    /// <summary>
    /// True if this result represents a duplicate key detection (idempotent replay).
    /// Duplicates are considered successful but may be tracked separately for observability.
    /// </summary>
    public bool IsDuplicate { get; init; }

    public static BatchAuditResult Succeeded(int eventCount) =>
        new() { Success = true, EventCount = eventCount };

    /// <summary>
    /// Creates a successful result that indicates duplicate detection.
    /// </summary>
    public static BatchAuditResult Duplicate(int eventCount) =>
        new() { Success = true, EventCount = eventCount, IsDuplicate = true };

    public static BatchAuditResult Failed(IReadOnlyList<AuditEvent> events, Exception ex) =>
        new() { Success = false, EventCount = events.Count, FailedEvents = events, Exception = ex };
}
