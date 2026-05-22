using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.DistributedLocking.Implementations;

/// <summary>
/// Null object pattern implementation for when distributed locking is not needed.
/// WARNING: This service provides NO mutual exclusion. It is intended ONLY for
/// single-threaded test scenarios or contexts where locking is explicitly disabled.
/// Using this in production with concurrent callers or multiple instances will
/// result in race conditions.
/// </summary>
public sealed class NullDistributedLockService(ILogger<NullDistributedLockService>? logger = null)
    : IAuditDistributedLockService
{
    /// <summary>
    /// Tracks whether a warning has been logged to avoid spamming logs on every call.
    /// </summary>
    private volatile bool _warningLogged;

    /// <summary>
    /// Returns a no-op lock handle immediately. This does NOT provide any mutual
    /// exclusion — concurrent callers will all receive a "lock" simultaneously.
    /// A warning is logged on first use to alert operators.
    /// </summary>
    public Task<IDisposable> AcquireLockAsync(
        string resource,
        TimeSpan expiry,
        CancellationToken cancellationToken = default)
    {
        if (!_warningLogged && logger != null)
        {
            _warningLogged = true;
            logger.LogWarning(
                "NullDistributedLockService.AcquireLockAsync called for resource '{Resource}'. " +
                "This service provides NO mutual exclusion — all callers succeed immediately. " +
                "If you are seeing this in production, verify that distributed locking is intentionally disabled.",
                resource);
        }

        return Task.FromResult<IDisposable>(NullLock.Instance);
    }

    /// <summary>
    /// Singleton no-op lock handle.
    /// </summary>
    private sealed class NullLock : IDisposable
    {
        /// <summary>
        /// Singleton instance of the no-op lock. All callers receive the same instance since it has no state and does not provide any actual locking.
        /// </summary>
        public static readonly NullLock Instance = new();

        /// <summary>
        /// Private constructor to prevent external instantiation. This class is a singleton and should only be accessed via the static Instance property.
        /// </summary>
        private NullLock()
        {
        }

        /// <summary>
        /// No-op dispose method. Does not release any lock since no lock is actually acquired.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
