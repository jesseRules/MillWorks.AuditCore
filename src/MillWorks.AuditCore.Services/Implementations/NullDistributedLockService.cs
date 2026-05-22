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
public sealed class NullDistributedLockService : IAuditDistributedLockService
{
    private readonly ILogger<NullDistributedLockService>? _logger;
    private volatile bool _warningLogged;

    public NullDistributedLockService(ILogger<NullDistributedLockService>? logger = null)
    {
        _logger = logger;
    }

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
        if (!_warningLogged && _logger != null)
        {
            _warningLogged = true;
            _logger.LogWarning(
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
        public static readonly NullLock Instance = new();
        private NullLock() { }
        public void Dispose() { }
    }
}