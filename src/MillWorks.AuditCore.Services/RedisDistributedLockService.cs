using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using StackExchange.Redis;

namespace MillWorks.AuditCore.Services.Redis;

/// <summary>
/// Redis-based distributed lock service for coordinating operations across multiple instances
/// </summary>
public sealed class RedisDistributedLockService : IAuditDistributedLockService
{
    /// <summary>
    /// Redis connection multiplexer
    /// </summary>
    private readonly IConnectionMultiplexer _redis;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<RedisDistributedLockService> _logger;

    /// <summary>
    /// Maximum number of attempts made before failing lock acquisition.
    /// </summary>
    private readonly int _maxRetries;

    /// <summary>
    /// Base delay used for exponential backoff between lock attempts.
    /// </summary>
    private readonly TimeSpan _baseDelay;

    /// <summary>
    /// Whether jitter should be added to the retry delay.
    /// </summary>
    private readonly bool _useJitter;

    /// <summary>
    /// Lock key prefix
    /// </summary>
    private const string _lockPrefix = "lock:";

    /// <summary>
    /// Creates the service and validates at startup that the backend supports the Lua scripting
    /// used for lock release (see <see cref="ValidateScriptingSupport"/>).
    /// </summary>
    /// <param name="redis">Connection multiplexer for the lock backend.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="maxRetries">Maximum acquisition attempts before timing out.</param>
    /// <param name="baseDelay">Base delay for exponential backoff between attempts.</param>
    /// <param name="useJitter">Whether to add jitter to the retry delay.</param>
    /// <param name="failFastOnMissingScripting">
    /// When true, a backend without Lua scripting support throws at construction instead of
    /// logging an error. Defaults to false so a misconfigured backend is surfaced loudly in
    /// logs at startup without preventing the host from starting.
    /// </param>
    public RedisDistributedLockService(
        IConnectionMultiplexer redis,
        ILogger<RedisDistributedLockService> logger,
        int maxRetries = 50,
        TimeSpan? baseDelay = null,
        bool useJitter = true,
        bool failFastOnMissingScripting = false)
    {
        _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _maxRetries = maxRetries > 0
            ? maxRetries
            : throw new ArgumentOutOfRangeException(nameof(maxRetries), "Max retries must be greater than zero");
        _baseDelay = baseDelay is { } delay && delay > TimeSpan.Zero
            ? delay
            : baseDelay is null
                ? TimeSpan.FromMilliseconds(50)
                : throw new ArgumentOutOfRangeException(nameof(baseDelay), "Base delay must be greater than zero");
        _useJitter = useJitter;

        ValidateScriptingSupport(failFastOnMissingScripting);
    }

    /// <summary>
    /// Verifies at startup that the backend supports the Lua scripting (<c>EVAL</c>) used to
    /// release locks. Some RESP-compatible servers — notably Garnet — ship with Lua scripting
    /// disabled by default; in that state lock release fails and a lock is only freed when its
    /// expiry elapses. Discovering this at startup is far better than at incident time.
    /// Skipped when the connection is not yet established (validation defers to first use).
    /// </summary>
    private void ValidateScriptingSupport(bool failFast)
    {
        if (!_redis.IsConnected)
        {
            _logger.LogDebug(
                "Distributed lock: skipping Lua scripting validation — backend is not connected at startup.");
            return;
        }

        try
        {
            // The same primitive lock release depends on; a trivial script keeps the probe cheap.
            _redis.GetDatabase().ScriptEvaluate("return 1");
            _logger.LogDebug("Distributed lock: Lua scripting (EVAL) is available on the backend.");
        }
        catch (RedisServerException ex) when (
            ex.Message.Contains("Lua", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
        {
            const string message =
                "Distributed lock backend does not have Lua scripting (EVAL) enabled, which lock " +
                "release requires. On Garnet, start the server with '--lua'. Until enabled, lock " +
                "release will fail and locks will only be freed when their expiry elapses.";

            if (failFast)
            {
                throw new InvalidOperationException(message, ex);
            }

            _logger.LogError(ex, "{Message}", message);
        }
        catch (Exception ex)
        {
            // Inconclusive probe (transient/permission error). Warn but do not block startup.
            _logger.LogWarning(ex,
                "Distributed lock: could not verify Lua scripting support at startup.");
        }
    }

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
