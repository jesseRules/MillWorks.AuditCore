namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Represents a message in the dead letter queue
/// </summary>
public class DeadLetterMessage<T>
{
    /// <summary>
    /// Identifier of the dead letter message
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Original message identifier
    /// </summary>
    public string? OriginalMessageId { get; set; }

    /// <summary>
    /// Message payload
    /// </summary>
    public T? Message { get; set; }

    /// <summary>
    /// Failure reason for dead lettering
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Exception details if available
    /// </summary>
    public string? ExceptionDetails { get; set; }

    /// <summary>
    /// Retry count for processing attempts
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Enqueue timestamp
    /// </summary>
    public DateTimeOffset EnqueuedAt { get; set; }

    /// <summary>
    /// Expiration timestamp
    /// </summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Metadata for quick lookups
/// </summary>
internal class MessageMetadata
{
    /// <summary>
    /// Identifier of the dead letter message
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Failure reason for dead lettering
    /// </summary>
    public string FailureReason { get; set; } = string.Empty;

    /// <summary>
    /// Retry count for processing attempts
    /// </summary>
    public int RetryCount { get; set; }

    /// <summary>
    /// Enqueue timestamp
    /// </summary>
    public DateTimeOffset EnqueuedAt { get; set; }
}

/// <summary>
/// Statistics about the dead letter queue
/// </summary>
public class QueueStatistics
{
    /// <summary>
    /// Total number of messages in the dead letter queue
    /// </summary>
    public long TotalMessages { get; set; }

    /// <summary>
    /// Oldest message date in the dead letter queue
    /// </summary>
    public DateTimeOffset? OldestMessageDate { get; set; }

    /// <summary>
    /// Newest message date in the dead letter queue
    /// </summary>
    public DateTimeOffset? NewestMessageDate { get; set; }

    /// <summary>
    /// Failure reasons with their respective counts
    /// </summary>
    public Dictionary<string, int> FailureReasons { get; set; } = new();
}