using Microsoft.Extensions.Options;

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
    /// Defaults to 24 hours. Set to <see cref="TimeSpan.Zero"/> to delete immediately on purge.
    /// Breaking change from v1 (was 7 days): reduced to minimize sensitive payload retention.
    /// </summary>
    public TimeSpan ProcessedRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum number of events the file-based DLQ will store before logging warnings.
    /// File DLQ is designed for small-volume use. For high-volume scenarios, use Redis DLQ.
    /// Default: 1000.
    /// </summary>
    public int FileBasedMaxQueueSize { get; set; } = 1000;

    /// <summary>
    /// When true, the application will throw at startup if the configured DLQ backing
    /// store (Redis or file system) is unreachable. When false (default), unreachability
    /// is logged as a Critical error but the application continues to start.
    /// Recommended: true for production deployments where DLQ availability is critical.
    /// </summary>
    public bool FailFastOnDlqUnavailable { get; set; }

    /// <summary>
    /// Hard capacity limit for the file-based DLQ. When the queue reaches this size,
    /// new events are rejected with a logged error instead of being silently dropped.
    /// Set to 0 to disable the hard cap (warning-only behavior via FileBasedMaxQueueSize).
    /// Default: 5000.
    /// </summary>
    public int FileBasedHardCapacity { get; set; } = 5000;
}

/// <summary>
/// Runtime validator for <see cref="ResilienceOptions"/>. Registered via the options
/// pipeline with <c>ValidateOnStart()</c> so misconfiguration fails at host boot.
/// </summary>
internal sealed class ResilienceOptionsValidator : IValidateOptions<ResilienceOptions>
{
    public ValidateOptionsResult Validate(string? name, ResilienceOptions options)
    {
        var failures = new List<string>();

        if (options.MaxRetries < 0)
        {
            failures.Add(
                $"{nameof(ResilienceOptions.MaxRetries)} must be >= 0.");
        }

        if (options.RetryDelaySeconds < 0)
        {
            failures.Add(
                $"{nameof(ResilienceOptions.RetryDelaySeconds)} must be >= 0.");
        }

        if (options.FileBasedMaxQueueSize <= 0)
        {
            failures.Add(
                $"{nameof(ResilienceOptions.FileBasedMaxQueueSize)} must be > 0.");
        }

        if (options.FileBasedHardCapacity < 0)
        {
            failures.Add(
                $"{nameof(ResilienceOptions.FileBasedHardCapacity)} must be >= 0.");
        }

        if (options.ProcessedRetention < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(ResilienceOptions.ProcessedRetention)} must be >= TimeSpan.Zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
