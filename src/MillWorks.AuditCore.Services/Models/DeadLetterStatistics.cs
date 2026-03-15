namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Statistics about the dead letter queue
/// </summary>
public sealed class DeadLetterStatistics
{
    /// <summary>
    /// Total number of events in the dead letter queue
    /// </summary>
    public int TotalEvents { get; set; }

    /// <summary>
    /// Processed events that have been successfully handled
    /// </summary>
    public int ProcessedEvents { get; set; }

    /// <summary>
    /// Pending events that are yet to be processed
    /// </summary>
    public int PendingEvents { get; set; }

    /// <summary>
    /// Failed events that could not be processed
    /// </summary>
    public int FailedEvents { get; set; }

    /// <summary>
    /// Oldest event date in the dead letter queue
    /// </summary>
    public DateTimeOffset? OldestEventDate { get; set; }

    /// <summary>
    /// Newest event date in the dead letter queue
    /// </summary>
    public DateTimeOffset? NewestEventDate { get; set; }

    /// <summary>
    /// Events grouped by type
    /// </summary>
    public Dictionary<string, int> EventsByType { get; set; } = new();

    /// <summary>
    /// Events grouped by failure reason
    /// </summary>
    public Dictionary<string, int> EventsByFailureReason { get; set; } = new();

    /// <summary>
    /// Total size of the dead letter queue in bytes
    /// </summary>
    public long TotalSizeBytes { get; set; }
}