namespace MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

/// <summary>
/// IDistributedLockService Interface
/// </summary>
public interface IAuditDistributedLockService
{
    /// <summary>
    /// Acquires a distributed lock for the specified resource with a given expiry time.
    /// </summary>
    /// <param name="resource"></param>
    /// <param name="expiry"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IDisposable> AcquireLockAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default);
}