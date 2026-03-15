namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Configuration options for tamper detection
/// </summary>
public class TamperDetectionOptions
{
    /// <summary>
    /// Use distributed locking (requires Redis)
    /// </summary>
    public bool UseDistributedLocking { get; set; }

    /// <summary>
    /// Maximum retry attempts for operations requiring locking
    /// </summary>
    public int MaxRetryAttempts { get; set; } = 10;

    /// <summary>
    /// Retry delay in milliseconds between attempts
    /// </summary>
    public int RetryDelayMilliseconds { get; set; } = 100;

    /// <summary>
    /// Lock timeout in seconds for distributed locks
    /// </summary>
    public int LockTimeoutSeconds { get; set; } = 5;
}