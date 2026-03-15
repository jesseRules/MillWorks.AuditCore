using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.DistributedLocking.Implementations;

/// <summary>
/// In-memory distributed lock service for testing and development
/// WARNING: This is NOT suitable for production use across multiple instances
/// </summary>
public class InMemoryDistributedLockService(ILogger<InMemoryDistributedLockService> logger)
    : IAuditDistributedLockService
{
    /// <summary>
    /// Locks store
    /// </summary>
    private readonly ConcurrentDictionary<string, LockInfo> _locks = new();

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<InMemoryDistributedLockService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Acquires a distributed lock for the specified resource with a given expiry time.
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiry"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="ArgumentException"></exception>
    /// <exception cref="TimeoutException"></exception>
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

        var lockValue = Guid.NewGuid().ToString();
        var expiresAt = DateTimeOffset.UtcNow.Add(expiry);
        var retryCount = 0;
        var maxRetries = 50;

        while (retryCount < maxRetries)
        {
            // Clean up expired locks
            CleanupExpiredLocks();

            // Try to acquire lock
            var lockInfo = new LockInfo(lockValue, expiresAt);

            if (_locks.TryAdd(resource, lockInfo))
            {
                _logger.LogDebug(
                    "Acquired in-memory lock for resource {Resource} (attempt {Attempt})",
                    resource, retryCount + 1);

                return new InMemoryLock(resource, lockValue, _locks, _logger);
            }

            retryCount++;

            if (retryCount >= maxRetries)
            {
                _logger.LogWarning(
                    "Failed to acquire in-memory lock for resource {Resource} after {MaxRetries} attempts",
                    resource, maxRetries);

                throw new TimeoutException(
                    $"Failed to acquire lock for resource '{resource}' after {maxRetries} attempts");
            }

            // Exponential backoff
            var delay = TimeSpan.FromMilliseconds(50 * Math.Pow(2, Math.Min(retryCount - 1, 5)));
            await Task.Delay(delay, cancellationToken);
        }

        throw new TimeoutException($"Failed to acquire lock for resource '{resource}'");
    }

    /// <summary>
    /// Cleans up expired locks from the in-memory store
    /// </summary>
    private void CleanupExpiredLocks()
    {
        var now = DateTimeOffset.UtcNow;
        var expiredKeys = _locks
            .Where(kvp => kvp.Value.ExpiresAt < now)
            .Select(static kvp => kvp.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _locks.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Lock information record
    /// </summary>
    /// <param name="Value"></param>
    /// <param name="ExpiresAt"></param>
    private sealed record LockInfo(string Value, DateTimeOffset ExpiresAt);

    /// <summary>
    /// In-memory lock implementation
    /// </summary>
    private sealed class InMemoryLock(
        string resource,
        string lockValue,
        ConcurrentDictionary<string, LockInfo> locks,
        ILogger logger)
        : IDisposable
    {
        /// <summary>
        /// Disposed flag
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Disposes the lock, releasing it from the in-memory store
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            try
            {
                // Only remove if we still own the lock
                if (locks.TryGetValue(resource, out var lockInfo) &&
                    lockInfo.Value == lockValue)
                {
                    locks.TryRemove(resource, out _);
                    logger.LogDebug("Released in-memory lock for {Resource}", resource);
                }
                else
                {
                    logger.LogWarning(
                        "Lock {Resource} was already released or expired",
                        resource);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error releasing in-memory lock for {Resource}", resource);
            }
            finally
            {
                _disposed = true;
            }
        }
    }
}