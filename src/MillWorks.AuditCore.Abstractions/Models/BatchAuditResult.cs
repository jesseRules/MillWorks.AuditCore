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

    public static BatchAuditResult Succeeded(int eventCount) =>
        new() { Success = true, EventCount = eventCount };

    public static BatchAuditResult Failed(IReadOnlyList<AuditEvent> events, Exception ex) =>
        new() { Success = false, EventCount = events.Count, FailedEvents = events, Exception = ex };
}
