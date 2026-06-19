namespace MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

/// <summary>
/// Coordinates work across instances via a best-effort distributed lock.
/// </summary>
/// <remarks>
/// <para>
/// This lock is an <b>efficiency optimization, not a correctness guarantee.</b> The lock is
/// granted with a TTL (<c>expiry</c>); if a holder's work outlives that TTL the key lapses
/// and a second caller can acquire the same lock, so two holders may run concurrently. No
/// fencing token is issued. This is inherent to TTL-based locks and cannot be fixed by
/// auto-renewal — a process pause or network delay can always let a lapsed lock be re-taken
/// (see Kleppmann, <i>How to do distributed locking</i>, 2016).
/// </para>
/// <para>
/// Therefore callers <b>must not</b> rely on this lock for correctness. Any operation that
/// must not be performed twice has to be made safe at the resource layer — via an atomic
/// claim/lease (as <c>AuditOutboxDrainer</c> does with its row leases) or an idempotent
/// write (as dead-letter reprocessing does: re-emitting an event collides on the
/// <c>EventId</c> primary key and is treated as success). The lock then only reduces
/// redundant work; transient overlap remains correct.
/// </para>
/// </remarks>
public interface IAuditDistributedLockService
{
    /// <summary>
    /// Acquires a best-effort distributed lock for the specified resource.
    /// </summary>
    /// <param name="resource">Logical name of the resource to coordinate on.</param>
    /// <param name="expiry">
    /// TTL after which the lock is released automatically. Work may outlive this, allowing
    /// transient overlap with another holder — see the type-level remarks. Choose an expiry
    /// comfortably longer than the expected work, but never treat it as a hard guarantee.
    /// </param>
    /// <param name="cancellationToken">Token to cancel waiting for the lock.</param>
    /// <returns>A handle that releases the lock (if still held) when disposed.</returns>
    Task<IDisposable> AcquireLockAsync(string resource, TimeSpan expiry, CancellationToken cancellationToken = default);
}