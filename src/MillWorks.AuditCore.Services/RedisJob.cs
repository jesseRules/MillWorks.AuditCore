namespace MillWorks.AuditCore.Services.Redis;

/// <summary>
/// Redis job model
/// </summary>
public sealed class RedisJob
{
    /// <summary>
    /// Identifier for the job, unique across the cluster
    /// </summary>
    public string Id { get; set; } = string.Empty;
    
    /// <summary>
    /// Queue name to which the job belongs
    /// </summary>
    public string QueueName { get; set; } = string.Empty;
    
    /// <summary>
    /// Job type identifier, used to route jobs to appropriate handlers
    /// </summary>
    public string JobType { get; set; } = string.Empty;
    
    /// <summary>
    /// Payload of the job, serialized as JSON
    /// </summary>
    public string Payload { get; set; } = string.Empty;
    
    /// <summary>
    /// Priority of the job, lower values indicate higher priority
    /// </summary>
    public int Priority { get; set; }
    
    /// <summary>
    /// Creation timestamp of the job
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
    
    /// <summary>
    /// Assigned worker node ID
    /// </summary>
    public DateTimeOffset? AssignedAt { get; set; }
    
    /// <summary>
    /// Assigned worker node ID
    /// </summary>
    public string? AssignedTo { get; set; }
    
    /// <summary>
    /// Maximum number of retries for the job
    /// </summary>
    public int MaxRetries { get; set; }
    
    /// <summary>
    /// Retry count for the job
    /// </summary>
    public int RetryCount { get; set; }
}