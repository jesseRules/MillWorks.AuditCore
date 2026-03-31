namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Resilience options for audit logging
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>
    /// Enable dead letter queue for failed audit events
    /// </summary>
    public bool EnableDeadLetterQueue { get; set; } = true;

    /// <summary>
    /// Dead letter queue provider
    /// </summary>
    public DeadLetterProvider DeadLetterProvider { get; set; } = DeadLetterProvider.FileSystem;

    /// <summary>
    /// Enable background processor for retrying failed events
    /// </summary>
    public bool EnableBackgroundProcessor { get; set; } = true;

    /// <summary>
    /// Maximum number of retries for failed audit events
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// Retry delay in seconds between attempts
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 1;

    /// <summary>
    /// When true, full exception stack traces are stored in DLQ entries.
    /// Defaults to false because stack traces can leak internal implementation details,
    /// provider errors, and sensitive values embedded in exception messages.
    /// </summary>
    public bool IncludeStackTraces { get; set; }

    /// <summary>
    /// How long processed DLQ artifacts are retained before cleanup.
    /// Applies to the file DLQ Processed folder and Redis processed entries.
    /// Defaults to 7 days. Set to <see cref="TimeSpan.Zero"/> to delete immediately on purge.
    /// </summary>
    public TimeSpan ProcessedRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Maximum number of events the file-based DLQ will store before logging warnings.
    /// File DLQ is designed for small-volume use. For high-volume scenarios, use Redis DLQ.
    /// Default: 1000.
    /// </summary>
    public int FileBasedMaxQueueSize { get; set; } = 1000;
}