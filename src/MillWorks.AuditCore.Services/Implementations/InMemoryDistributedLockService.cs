using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.DistributedLocking.Implementations;

/// <summary>
/// In-memory distributed lock service for testing and development.
/// WARNING: This is NOT suitable for production use across multiple instances.
/// <para>
/// In-process semantics: an acquired lock lives until <see cref="IDisposable.Dispose"/> is
/// called on the returned handle. The <c>expiry</c> parameter is retained for interface
/// compatibility with <see cref="IAuditDistributedLockService"/> and is recorded informationally
/// on the <c>LockInfo</c> record, but it is NOT enforced as a lease TTL. Specifically, a held
/// lock cannot be re-acquired by another caller after <c>expiry</c> elapses unless the original
/// holder disposes the handle. This avoids the lease-race that allowed two writers into the
/// integrity-chain critical section under long-running batch writes (Phase 6.5 finding).
/// </para>
/// </summary>
public class InMemoryDistributedLockService(ILogger<InMemoryDistributedLockService> logger)
    : IAuditDistributedLockService
{
    /// <summary>
    /// Process-wide lock store. Static so the in-memory lock serializes across all
    /// instances of this service within one process, regardless of how the service is
    /// registered in DI. Mutual exclusion must hold at the process level for the
    /// integrity-chain critical section in <c>TamperDetectionService</c>.
    /// </summary>
    private static readonly ConcurrentDictionary<string, LockInfo> _locks = new();

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<InMemoryDistributedLockService> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>
    /// Acquires a distributed lock for the specified resource.
    /// </summary>
    /// <param name="resource">The resource key to lock.</param>
    /// <param name="expiry">
    /// Recorded informationally on the lock entry; not enforced as a lease TTL by this
    /// in-process implementation. The held lock lives until <see cref="IDisposable.Dispose"/>.
    /// Required to be greater than <see cref="TimeSpan.Zero"/> for interface-contract validation.
    /// </param>
    /// <param name="cancellationToken">Cancellation token for the acquisition retry loop.</param>
    /// <returns>A disposable lock handle. Disposing it releases the lock.</returns>
    /// <exception cref="ArgumentException"><paramref name="expiry"/> is non-positive.</exception>
    /// <exception cref="TimeoutException">Acquisition retries exhausted without success.</exception>
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
            // Try to acquire lock. We do NOT sweep expired entries — a held lock cannot be
            // forced out by TTL. Releases happen exclusively through InMemoryLock.Dispose.
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
    /// Lock information record. <see cref="ExpiresAt"/> is informational only — see the
    /// class header for why TTL-based cleanup was removed.
    /// </summary>
    /// <param name="Value">Per-acquisition lock token; matched on Dispose to prevent stale-release.</param>
    /// <param name="ExpiresAt">Informational nominal expiry; not enforced by this implementation.</param>
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