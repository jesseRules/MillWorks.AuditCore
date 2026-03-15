namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// JobQueueConfiguration defines the configuration settings for the job queue system.
/// </summary>
public sealed class JobQueueConfiguration
{
    /// <summary>
    /// MaxConcurrentJobs defines the maximum number of jobs that can run concurrently across the entire job queue.
    /// </summary>
    public int MaxConcurrentJobs { get; set; } = 10;
    
    /// <summary>
    /// JobTimeout defines the maximum time a job can run before it is considered dead.
    /// </summary>
    public TimeSpan JobTimeout { get; set; } = TimeSpan.FromMinutes(30);
    
    /// <summary>
    /// DeadJobCheckInterval defines how often the system checks for dead jobs.
    /// </summary>
    public TimeSpan DeadJobCheckInterval { get; set; } = TimeSpan.FromMinutes(5);
    
    /// <summary>
    /// QueueConcurrencyLimits defines the maximum number of concurrent jobs allowed for each queue.
    /// </summary>
    public Dictionary<string, int> QueueConcurrencyLimits { get; set; } = new();

    /// <summary>
    /// MaxRetries defines the maximum number of retries for a failed job.
    /// </summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>
    /// RetryDelaySeconds defines the delay in seconds before retrying a failed job.
    /// </summary>
    public int RetryDelaySeconds { get; set; } = 10;
}