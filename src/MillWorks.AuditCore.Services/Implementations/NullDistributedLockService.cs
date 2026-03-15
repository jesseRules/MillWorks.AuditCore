using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.DistributedLocking.Implementations;

/// <summary>
/// Null object pattern implementation for when distributed locking is not needed
/// </summary>
public sealed class NullDistributedLockService : IAuditDistributedLockService
{
    /// <summary>
    /// Acquires a no-op distributed lock for the specified resource.
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiry"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public Task<IDisposable> AcquireLockAsync(
        string resource, 
        TimeSpan expiry, 
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult<IDisposable>(new NullLock());
    }

    /// <summary>
    /// No-op lock implementation
    /// </summary>
    private sealed class NullLock : IDisposable
    {
        /// <summary>
        /// Disposes the no-op lock
        /// </summary>
        public void Dispose() { }
    }
}