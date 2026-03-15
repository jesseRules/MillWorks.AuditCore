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
}