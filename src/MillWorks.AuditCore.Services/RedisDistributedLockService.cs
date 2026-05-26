using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Services.Redis;

/// <summary>
/// Redis-based distributed lock service for coordinating operations across multiple instances
/// </summary>
public sealed class RedisDistributedLockService(
    IConnectionMultiplexer redis,
    ILogger<RedisDistributedLockService> logger,
    int maxRetries = 50,
    TimeSpan? baseDelay = null,
    bool useJitter = true)
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
    /// Maximum number of attempts made before failing lock acquisition.
    /// </summary>
    private readonly int _maxRetries = maxRetries > 0
        ? maxRetries
        : throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries must be greater than zero");

    /// <summary>
    /// Base delay used for exponential backoff between lock attempts.
    /// </summary>
    private readonly TimeSpan _baseDelay = baseDelay is { } delay && delay > TimeSpan.Zero
        ? delay
        : baseDelay is null
            ? TimeSpan.FromMilliseconds(50)
            : throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be greater than zero");

    /// <summary>
    /// Whether jitter should be added to the retry delay.
    /// </summary>
    private readonly bool _useJitter = useJitter;

    /// <summary>
    /// Lock key prefix
    /// </summary>
    private const string _lockPrefix = "lock:";

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

        var lockKey = $"{_lockPrefix}{resource}";
        var lockValue = Guid.NewGuid().ToString();
        var db = _redis.GetDatabase();

        // Try to acquire lock with exponential backoff
        var acquired = false;
        var retryCount = 0;
        while (!acquired && retryCount < _maxRetries)
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

            if (retryCount >= _maxRetries)
            {
                _logger.LogWarning(
                    "Failed to acquire distributed lock for resource {Resource} after {MaxRetries} attempts",
                    resource, _maxRetries);

                throw new TimeoutException(
                    $"Failed to acquire distributed lock for resource '{resource}' after {_maxRetries} attempts");
            }

            // Exponential backoff with jitter
            var delay = TimeSpan.FromMilliseconds(
                _baseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(retryCount - 1, 5)));

            var jitter = _useJitter
                ? TimeSpan.FromMilliseconds(Random.Shared.Next(0, Math.Max(1, (int)(delay.TotalMilliseconds * 0.3))))
                : TimeSpan.Zero;

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
