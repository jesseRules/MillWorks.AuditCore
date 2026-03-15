using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Services.Core;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Services.Redis;

/// <summary>
/// Redis-based job queue for high-throughput job processing
/// </summary>
public sealed class RedisJobQueue : IDisposable
{
    /// <summary>
    /// Redis connection multiplexer
    /// </summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Configuration settings
    /// </summary>
    private readonly JobQueueConfiguration _config;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<RedisJobQueue> _logger;

    /// <summary>
    /// Database instance
    /// </summary>
    private readonly IDatabase _db;

    /// <summary>
    /// Queue prefix
    /// </summary>
    private const string QueuePrefix = "jobs:queue:";

    /// <summary>
    /// Processing queue prefix
    /// </summary>
    private const string ProcessingPrefix = "jobs:processing:";

    /// <summary>
    /// Dead letter queue prefix
    /// </summary>
    private const string DeadLetterPrefix = "jobs:deadletter:";

    /// <summary>
    /// RedisJobQueue constructor
    /// </summary>
    /// <param name="redis"></param>
    /// <param name="config"></param>
    /// <param name="logger"></param>
    public RedisJobQueue(
        IConnectionMultiplexer redis,
        IOptions<JobQueueConfiguration> config,
        ILogger<RedisJobQueue> logger)
    {
        _redis = redis;
        _config = config.Value;
        _logger = logger;
        _db = _redis.GetDatabase();
    }

    /// <summary>
    /// Enqueue a job to Redis
    /// </summary>
    public async Task<string> EnqueueAsync(string queueName, string jobType, string payload, int priority = 0)
    {
        var jobId = Guid.NewGuid().ToString();
        var job = new RedisJob
        {
            Id = jobId,
            QueueName = queueName,
            JobType = jobType,
            Payload = payload,
            Priority = priority,
            CreatedAt = DateTimeOffset.UtcNow,
            MaxRetries = 3,
            RetryCount = 0
        };

        var queueKey = $"{QueuePrefix}{queueName}";
        var score = GetPriorityScore(priority);

        await _db.SortedSetAddAsync(queueKey, JsonSerializer.Serialize(job), score);

        _logger.LogDebug("Enqueued job {JobId} to queue {QueueName}", jobId, queueName);

        return jobId;
    }

    /// <summary>
    /// Dequeue a job for processing
    /// </summary>
    public async Task<RedisJob?> DequeueAsync(string queueName, string workerId)
    {
        var queueKey = $"{QueuePrefix}{queueName}";
        var processingKey = $"{ProcessingPrefix}{workerId}";

        // Use Lua script to atomically move job from queue to processing
        var script = @"
            local job = redis.call('zpopmin', KEYS[1])
            if #job > 0 then
                redis.call('hset', KEYS[2], job[1], ARGV[1])
                redis.call('expire', KEYS[2], ARGV[2])
                return job[1]
            end
            return nil";

        var result = await _db.ScriptEvaluateAsync(script,
            [queueKey, processingKey],
            [DateTimeOffset.UtcNow.ToString("O"), (int)_config.JobTimeout.TotalSeconds]);

        if (result.IsNull) return null;

        try
        {
            var job = JsonSerializer.Deserialize<RedisJob>(result.ToString());
            if (job == null) return job;
            job.AssignedTo = workerId;
            job.AssignedAt = DateTimeOffset.UtcNow;
            _logger.LogDebug("Dequeued job {JobId} from queue {QueueName}", job.Id, queueName);

            return job;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to deserialize job");
            return null;
        }
    }

    /// <summary>
    /// Complete a job
    /// </summary>
    public async Task CompleteAsync(string jobId, string workerId, bool success, string? error = null)
    {
        var processingKey = $"{ProcessingPrefix}{workerId}";

        // Remove from processing
        await _db.HashDeleteAsync(processingKey, jobId);

        if (!success)
        {
            // Handle failure - might need to retry or move to dead letter
            _logger.LogWarning("Job {JobId} failed: {Error}", jobId, error);
        }
        else
        {
            _logger.LogDebug("Job {JobId} completed successfully", jobId);
        }
    }

    /// <summary>
    /// Recover stuck jobs
    /// </summary>
    public async Task RecoverStuckJobsAsync()
    {
        var pattern = $"{ProcessingPrefix}*";

        await foreach (var key in GetKeysAsync(pattern))
        {
            var ttl = await _db.KeyTimeToLiveAsync(key);
            if (ttl == null || ttl.Value.TotalSeconds <= 0)
            {
                // Key expired, jobs are stuck
                var jobs = await _db.HashGetAllAsync(key);
                foreach (var job in jobs)
                {
                    try
                    {
                        var jobData = JsonSerializer.Deserialize<RedisJob>((string)job.Value!);
                        if (jobData == null) continue;
                        // Re-enqueue or move to dead letter
                        if (jobData.RetryCount < jobData.MaxRetries)
                        {
                            jobData.RetryCount++;
                            var queueKey = $"{QueuePrefix}{jobData.QueueName}";
                            await _db.SortedSetAddAsync(queueKey,
                                JsonSerializer.Serialize(jobData),
                                GetPriorityScore(jobData.Priority));

                            _logger.LogInformation("Recovered stuck job {JobId}", jobData.Id);
                        }
                        else
                        {
                            // Move to dead letter
                            var deadLetterKey = $"{DeadLetterPrefix}{jobData.QueueName}";
                            await _db.ListRightPushAsync(deadLetterKey, JsonSerializer.Serialize(jobData));

                            _logger.LogWarning("Moved job {JobId} to dead letter queue", jobData.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to recover job");
                    }
                }

                // Delete the expired processing key
                await _db.KeyDeleteAsync(key);
            }
        }
    }

    /// <summary>
    /// Get priority score for sorted set
    /// </summary>
    /// <param name="priority"></param>
    /// <returns></returns>
    private double GetPriorityScore(int priority)
    {
        // Lower score = higher priority
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - priority * 1000;
    }

    /// <summary>
    /// Get keys matching pattern
    /// </summary>
    /// <param name="pattern"></param>
    /// <returns></returns>
    private async IAsyncEnumerable<RedisKey> GetKeysAsync(string pattern)
    {
        foreach (var endpoint in _redis.GetEndPoints())
        {
            var server = _redis.GetServer(endpoint);
            await foreach (var key in server.KeysAsync(pattern: pattern))
            {
                yield return key;
            }
        }
    }

    /// <summary>
    /// Dispose resources
    /// </summary>
    public void Dispose()
    {
        // Cleanup if needed
        GC.SuppressFinalize(this);
    }
}