using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Services.Redis;

/// <summary>
/// Redis-based distributed lock service for coordinating operations across multiple instances
/// </summary>
public sealed class RedisDistributedLockService(
    IConnectionMultiplexer redis,
    ILogger<RedisDistributedLockService> logger)
    : IAuditDistributedLockService
{
    /// <summary>
    /// Redis connection multiplexer
    /// </summary>
    private readonly IConnectionMultiplexer _redis = redis ?? throw new ArgumentNullException(nameof(redis));

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<RedisDistributedLockService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Lock key prefix
    /// </summary>
    private const string LockPrefix = "lock:";

    /// <summary>
    /// Acquires a distributed lock for the specified resource with a given expiry time
    /// </summary>
    public async Task<IDisposable> AcquireLockAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (expiry <= TimeSpan.Zero)
        {
            throw new ArgumentException("Expiry must be greater than zero", nameof(expiry));
        }

        var lockKey = $"{LockPrefix}{resource}";
        var lockValue = Guid.NewGuid().ToString();
        var db = _redis.GetDatabase();

        // Try to acquire lock with exponential backoff
        var acquired = false;
        var retryCount = 0;
        var maxRetries = 50;
        var baseDelay = TimeSpan.FromMilliseconds(50);

        while (!acquired && retryCount < maxRetries)
        {
            // Use SET NX (set if not exists) with expiry
            acquired = await db.StringSetAsync(
                lockKey,
                lockValue,
                expiry,
                When.NotExists);

            if (acquired)
            {
                // Successfully acquired the lock, break out of loop
                break; 
            }

            retryCount++;

            if (retryCount >= maxRetries)
            {
                _logger.LogWarning(
                    "Failed to acquire distributed lock for resource {Resource} after {MaxRetries} attempts",
                    resource, maxRetries);

                throw new TimeoutException(
                    $"Failed to acquire distributed lock for resource '{resource}' after {maxRetries} attempts");
            }

            // Exponential backoff with jitter
            var delay = TimeSpan.FromMilliseconds(
                baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retryCount - 1, 5)));

            var jitter = TimeSpan.FromMilliseconds(
                Random.Shared.Next(0, (int)(delay.TotalMilliseconds * 0.3)));

            await Task.Delay(delay + jitter, cancellationToken);
        }

        _logger.LogDebug(
            "Acquired distributed lock for resource {Resource} with value {LockValue} (attempt {Attempt})",
            resource, lockValue, retryCount + 1);

        return new RedisLock(db, lockKey, lockValue, _logger);
    }

    /// <summary>
    /// Internal class representing a Redis lock
    /// </summary>
    private sealed class RedisLock(
        IDatabase db,
        string lockKey,
        string lockValue,
        ILogger logger)
        : IDisposable
    {
        /// <summary>
        /// Database instance
        /// </summary>
        private readonly IDatabase _db = db;

        /// <summary>
        /// Lock key
        /// </summary>
        private readonly string _lockKey = lockKey;

        /// <summary>
        /// Disposed flag
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Disposes the lock, releasing it in Redis
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Only delete the lock if we still own it (compare value)
                // Use Lua script to ensure atomicity
                var script = @"
                    if redis.call('get', KEYS[1]) == ARGV[1] then
                        return redis.call('del', KEYS[1])
                    else
                        return 0
                    end";

                var result = _db.ScriptEvaluate(
                    script,
                    [_lockKey],
                    [lockValue]);

                if (result.IsNull || (int)result == 0)
                {
                    logger.LogWarning(
                        "Lock {LockKey} was already released or expired",
                        _lockKey);
                }
                else
                {
                    logger.LogDebug(
                        "Released distributed lock for {LockKey}",
                        _lockKey);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Error releasing distributed lock for {LockKey}",
                    _lockKey);
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}