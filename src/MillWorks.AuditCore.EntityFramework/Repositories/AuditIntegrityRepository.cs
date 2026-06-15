using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// AuditIntegrityRepository provides methods to interact with audit integrity records for tamper evidence and blockchain-style verification.
/// </summary>
/// <param name="context"></param>
public sealed class AuditIntegrityRepository(AuditDbContext context)
    : Repository<AuditIntegrityEntity>(context), IAuditIntegrityRepository
{
    /// <summary>
    /// Gets the audit integrity record for a specific event.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditIntegrityEntity?> GetByEventIdAsync(Guid eventId,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .FirstOrDefaultAsync(ai => ai.EventId == eventId, cancellationToken);
    }

    /// <summary>
    /// Gets the latest audit integrity record by sequence number.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditIntegrityEntity?> GetLatestBySequenceAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .OrderByDescending(static ai => ai.SequenceNumber)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Gets audit integrity records within a sequence number range.
    /// </summary>
    /// <param name="startSequence"></param>
    /// <param name="endSequence"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditIntegrityEntity>> GetBySequenceRangeAsync(long startSequence, long endSequence,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ai => ai.SequenceNumber >= startSequence && ai.SequenceNumber <= endSequence)
            .OrderBy(static ai => ai.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Validates the integrity chain by checking hash linkage between consecutive records.
    /// Returns a result that distinguishes "no data" from "valid" and validates boundary
    /// sequence numbers against the requested range (#6).
    /// </summary>
    /// <param name="startSequence"></param>
    /// <param name="endSequence"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>A result containing validity status and details about any issues found.</returns>
    public async Task<ChainValidationResult> ValidateIntegrityChainWithDetailsAsync(
        long startSequence,
        long endSequence,
        CancellationToken cancellationToken = default)
    {
        var records = await DbSet.AsNoTracking()
            .Where(ai => ai.SequenceNumber >= startSequence && ai.SequenceNumber <= endSequence)
            .OrderBy(static ai => ai.SequenceNumber)
            .ToListAsync(cancellationToken);

        if (records.Count == 0)
        {
            return new ChainValidationResult
            {
                IsValid = false,
                IsEmpty = true,
                Message = $"No records found in range [{startSequence}, {endSequence}]"
            };
        }

        // #6: Validate boundary sequence numbers against the requested range
        if (records[0].SequenceNumber != startSequence)
        {
            return new ChainValidationResult
            {
                IsValid = false,
                Message = $"First record sequence {records[0].SequenceNumber} does not match requested start {startSequence} (possible truncation)"
            };
        }

        if (records[^1].SequenceNumber != endSequence)
        {
            return new ChainValidationResult
            {
                IsValid = false,
                Message = $"Last record sequence {records[^1].SequenceNumber} does not match requested end {endSequence} (possible truncation)"
            };
        }

        // Validate chain linkage
        for (int i = 1; i < records.Count; i++)
        {
            var current = records[i];
            var previous = records[i - 1];

            // Check if current record's PreviousEventHash matches previous record's EventHash
            if (current.PreviousEventHash != previous.EventHash)
            {
                return new ChainValidationResult
                {
                    IsValid = false,
                    Message = $"Chain broken at sequence {current.SequenceNumber}: PreviousEventHash does not match"
                };
            }

            // Verify sequence continuity
            if (current.SequenceNumber != previous.SequenceNumber + 1)
            {
                return new ChainValidationResult
                {
                    IsValid = false,
                    Message = $"Sequence gap between {previous.SequenceNumber} and {current.SequenceNumber}"
                };
            }
        }

        return new ChainValidationResult
        {
            IsValid = true,
            RecordCount = records.Count
        };
    }

    /// <summary>
    /// Validates the integrity chain by checking hash linkage between consecutive records.
    /// Legacy method - use <see cref="ValidateIntegrityChainWithDetailsAsync"/> for boundary validation.
    /// </summary>
    /// <param name="startSequence"></param>
    /// <param name="endSequence"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<bool> ValidateIntegrityChainAsync(long startSequence, long endSequence,
        CancellationToken cancellationToken = default)
    {
        var result = await ValidateIntegrityChainWithDetailsAsync(startSequence, endSequence, cancellationToken);
        // For backward compatibility, treat empty as valid (callers may depend on this)
        return result.IsValid || result.IsEmpty;
    }

    /// <summary>
    /// Returns MAX(SequenceNumber) + 1 across the integrity table. Safe under the integrity
    /// append lock held by <c>TamperDetectionService</c>; not safe to call outside that lock.
    /// </summary>
    public async Task<long> GetNextSequenceNumberAsync(CancellationToken cancellationToken = default)
    {
        var maxSequence = await DbSet.AsNoTracking()
            .MaxAsync(static ai => (long?)ai.SequenceNumber, cancellationToken);

        return (maxSequence ?? 0) + 1;
    }

    /// <inheritdoc />
    public bool SupportsCrossProcessAppendLock => Context.Database.IsSqlServer();

    /// <inheritdoc />
    public async Task AcquireAppendLockAsync(CancellationToken cancellationToken = default)
    {
        // Non-SQL-Server providers (SQLite in tests) don't support sp_getapplock.
        // SQLite serializes writes natively; the caller checks
        // SupportsCrossProcessAppendLock and arranges process-local serialization.
        if (!Context.Database.IsSqlServer())
        {
            return;
        }

        var transaction = Context.Database.CurrentTransaction
                          ?? throw new InvalidOperationException(
                              "AcquireAppendLockAsync must be called inside an active transaction.");

        var connection = Context.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = ((IInfrastructure<DbTransaction>)transaction).Instance;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.CommandText = "sp_getapplock";
        // Slightly above @LockTimeout so a timed-out applock surfaces via the return
        // code rather than a command-level SQL timeout.
        cmd.CommandTimeout = 35;

        var returnValue = cmd.CreateParameter();
        returnValue.Direction = ParameterDirection.ReturnValue;
        returnValue.DbType = DbType.Int32;
        cmd.Parameters.Add(returnValue);

        AddInputParameter(cmd, "@Resource", DbType.String, "audit:integrity:append");
        AddInputParameter(cmd, "@LockMode", DbType.String, "Exclusive");
        AddInputParameter(cmd, "@LockOwner", DbType.String, "Transaction");
        AddInputParameter(cmd, "@LockTimeout", DbType.Int32, 30_000);

        await cmd.ExecuteNonQueryAsync(cancellationToken);

        // sp_getapplock return codes:
        //   0  = granted synchronously
        //   1  = granted after waiting
        //  -1  = lock request timed out
        //  -2  = lock request cancelled
        //  -3  = lock request chosen as deadlock victim
        // -999 = parameter/call fault
        var code = returnValue.Value is int v ? v : -999;
        if (code < 0)
        {
            throw new TimeoutException(
                $"Failed to acquire audit:integrity:append applock (sp_getapplock returned {code}).");
        }
    }

    /// <summary>
    /// Helper method to add input parameters to a DbCommand for sp_getapplock.
    /// </summary>
    /// <param name="cmd"></param>
    /// <param name="name"></param>
    /// <param name="type"></param>
    /// <param name="value"></param>
    private static void AddInputParameter(DbCommand cmd, string name, DbType type, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.DbType = type;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    /// <summary>
    /// Gets audit integrity records by algorithm version.
    /// </summary>
    /// <param name="algorithmVersion"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditIntegrityEntity>> GetByAlgorithmVersionAsync(int algorithmVersion,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ai => ai.AlgorithmVersion == algorithmVersion)
            .OrderBy(static ai => ai.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets audit integrity records within a trusted timestamp range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditIntegrityEntity>> GetByTrustedTimestampRangeAsync(DateTimeOffset startDate,
        DateTimeOffset endDate, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ai => ai.TrustedTimestamp >= startDate && ai.TrustedTimestamp <= endDate)
            .OrderBy(static ai => ai.TrustedTimestamp)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets audit integrity records with their associated audit events ordered by sequence number.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditIntegrityEntity>> GetWithAuditEventsAsync(DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null, CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking()
            .Include(static i => i.AuditEvent)
            .AsQueryable();

        if (startDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp <= endDate.Value);
        }

        return await query
            .OrderBy(static i => i.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a page of audit integrity records with their associated audit events, ordered by sequence number.
    /// </summary>
    public async Task<List<AuditIntegrityEntity>> GetWithAuditEventsPagedAsync(
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking()
            .Include(static i => i.AuditEvent)
            .AsQueryable();

        if (startDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp <= endDate.Value);
        }

        return await query
            .OrderBy(static i => i.SequenceNumber)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the count of audit integrity records within an optional date range.
    /// </summary>
    public async Task<int> GetCountAsync(
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking().AsQueryable();

        if (startDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp >= startDate.Value);
        }

        if (endDate.HasValue)
        {
            query = query.Where(i => i.TrustedTimestamp <= endDate.Value);
        }

        return await query.CountAsync(cancellationToken);
    }

    /// <summary>
    /// Gets all sequence numbers in order for integrity verification.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<long>> GetAllSequenceNumbersAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .OrderBy(static ai => ai.SequenceNumber)
            .Select(static ai => ai.SequenceNumber)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AuditIntegrityEntity> StreamByEventIdsAsync(
        IReadOnlyList<Guid> eventIds,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        const int batchSize = 500;

        for (var offset = 0; offset < eventIds.Count; offset += batchSize)
        {
            var end = Math.Min(offset + batchSize, eventIds.Count);
            var batch = new Guid[end - offset];
            for (var i = offset; i < end; i++)
            {
                batch[i - offset] = eventIds[i];
            }

            var records = await DbSet.AsNoTracking()
                .Where(ai => ((IEnumerable<Guid>)batch).Contains(ai.EventId))
                .OrderBy(static ai => ai.SequenceNumber)
                .ToListAsync(cancellationToken);

            foreach (var record in records)
            {
                yield return record;
            }
        }
    }
}
