namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Result of reprocessing operations
/// </summary>
public sealed class ReprocessingResult
{
    /// <summary>
    /// Total number of events to reprocess
    /// </summary>
    public int TotalEvents { get; set; }

    /// <summary>
    /// Successfully processed events
    /// </summary>
    public int SuccessfullyProcessed { get; set; }

    /// <summary>
    /// Failed events that could not be processed
    /// </summary>
    public int FailedToProcess { get; set; }

    /// <summary>
    /// Failed event IDs that could not be reprocessed
    /// </summary>
    public List<string> FailedEventIds { get; set; } = new();

    /// <summary>
    /// Duration of the reprocessing operation
    /// </summary>
    public TimeSpan Duration { get; set; }
}