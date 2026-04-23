using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

/// <summary>
/// Interface for Audit Integrity Repository
/// </summary>
public interface IAuditIntegrityRepository : IRepository<AuditIntegrityEntity>
{
    /// <summary>
    /// Gets the audit integrity record for a specific event.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditIntegrityEntity?> GetByEventIdAsync(Guid eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the latest audit integrity record by sequence number.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditIntegrityEntity?> GetLatestBySequenceAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit integrity records within a sequence number range.
    /// </summary>
    /// <param name="startSequence"></param>
    /// <param name="endSequence"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditIntegrityEntity>> GetBySequenceRangeAsync(long startSequence, long endSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates the integrity chain by checking hash linkage between consecutive records.
    /// </summary>
    /// <param name="startSequence"></param>
    /// <param name="endSequence"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> ValidateIntegrityChainAsync(long startSequence, long endSequence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns MAX(SequenceNumber) + 1 across the integrity table. Safe under the integrity
    /// append lock held by <c>TamperDetectionService</c>; not safe to call outside that lock.
    /// </summary>
    Task<long> GetNextSequenceNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when <see cref="AcquireAppendLockAsync"/> takes a real cross-process lock on
    /// the active EF provider (SQL Server's <c>sp_getapplock</c>). False when it is a
    /// no-op (e.g. the SQLite test provider); in that case the caller is responsible
    /// for process-local serialization of the hash-chain append.
    /// </summary>
    bool SupportsCrossProcessAppendLock { get; }

    /// <summary>
    /// Acquires an exclusive database-level lock named <c>audit:integrity:append</c> bound to
    /// the current transaction. Serializes hash-chain appends across every process talking
    /// to the same database, removing the duplicate-key race on
    /// <c>AuditIntegrity.SequenceNumber</c>.
    /// <para>
    /// On SQL Server, uses <c>sp_getapplock</c> with <c>LockOwner='Transaction'</c>; the lock
    /// auto-releases when the enclosing transaction commits, rolls back, or the connection
    /// drops — there is no lease TTL to expire mid-critical-section. On other providers
    /// (e.g. SQLite test harness) this is a no-op; <see cref="SupportsCrossProcessAppendLock"/>
    /// tells the caller whether to arrange its own serialization.
    /// </para>
    /// <para>Must be called inside an active transaction.</para>
    /// </summary>
    /// <exception cref="InvalidOperationException">No active transaction.</exception>
    /// <exception cref="TimeoutException"><c>sp_getapplock</c> returned a negative code
    /// (timeout, cancel, deadlock, or parameter fault).</exception>
    Task AcquireAppendLockAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit integrity records by algorithm version.
    /// </summary>
    /// <param name="algorithmVersion"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditIntegrityEntity>> GetByAlgorithmVersionAsync(int algorithmVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit integrity records within a trusted timestamp range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditIntegrityEntity>> GetByTrustedTimestampRangeAsync(DateTimeOffset startDate,
        DateTimeOffset endDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets audit integrity records with their associated audit events ordered by sequence number.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditIntegrityEntity>> GetWithAuditEventsAsync(DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of audit integrity records with their associated audit events, ordered by sequence number.
    /// Use this instead of <see cref="GetWithAuditEventsAsync"/> when the result set may be large.
    /// </summary>
    /// <param name="startDate">Optional start date filter on TrustedTimestamp.</param>
    /// <param name="endDate">Optional end date filter on TrustedTimestamp.</param>
    /// <param name="skip">Number of records to skip.</param>
    /// <param name="take">Number of records to return.</param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<List<AuditIntegrityEntity>> GetWithAuditEventsPagedAsync(
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the count of audit integrity records within an optional date range.
    /// </summary>
    Task<int> GetCountAsync(
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all sequence numbers in order for integrity verification.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<long>> GetAllSequenceNumbersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams integrity records for the given event IDs in chunks, without buffering the
    /// full result set. IDs are queried in batches to stay within relational parameter limits.
    /// Intended for archival and bulk export scenarios.
    /// </summary>
    IAsyncEnumerable<AuditIntegrityEntity> StreamByEventIdsAsync(
        IReadOnlyList<Guid> eventIds,
        CancellationToken cancellationToken = default);
}