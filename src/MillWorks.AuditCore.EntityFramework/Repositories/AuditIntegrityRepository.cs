using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
                Message =
                    $"First record sequence {records[0].SequenceNumber} does not match requested start {startSequence} (possible truncation)"
            };
        }

        if (records[^1].SequenceNumber != endSequence)
        {
            return new ChainValidationResult
            {
                IsValid = false,
                Message =
                    $"Last record sequence {records[^1].SequenceNumber} does not match requested end {endSequence} (possible truncation)"
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

    /// <inheritdoc />
    public async Task<List<AuditIntegrityEntity>> GetWithAuditEventsAfterSequenceAsync(
        DateTimeOffset? startDate,
        DateTimeOffset? endDate,
        long afterSequenceNumber,
        int take,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking()
            .Include(static i => i.AuditEvent)
            .Where(i => i.SequenceNumber > afterSequenceNumber);

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

    /// <inheritdoc />
    public async Task<bool> IsSequenceContiguousAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Context.Database.IsSqlServer())
            return await ReadSequenceSummaryAsync(Context, cancellationToken);

        // Do not inherit an ambient/caller transaction, a possibly weaker isolation level,
        // or its tracked changes. Each retry gets its own connection, context and transaction.
        using var suppressed = new System.Transactions.TransactionScope(
            System.Transactions.TransactionScopeOption.Suppress,
            System.Transactions.TransactionScopeAsyncFlowOption.Enabled);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // Reserve the last 250 ms of the seven-second budget for transaction/connection cleanup.
        deadline.CancelAfter(TimeSpan.FromMilliseconds(6750));
        try
        {
            for (var attempt = 0;; attempt++)
            {
                try
                {
                    return await ReadSerializableSequenceSummaryAsync(deadline.Token);
                }
                catch (SqlException ex) when (attempt < 2 &&
                                              ex.Number is 1205 or 1222 or -2 && !deadline.IsCancellationRequested)
                {
                    // A failed/blocked read is not corruption. Retry only transient database
                    // failures, never a valid summary that actually reports a gap/duplicate.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
                }
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested &&
                                                    deadline.IsCancellationRequested)
        {
            throw new TimeoutException("Sequence validation did not complete within its seven-second deadline.", ex);
        }
    }

    private async Task<bool> ReadSerializableSequenceSummaryAsync(CancellationToken cancellationToken)
    {
        // Clone preserves SqlCredential/token authentication as well as connection-string
        // credentials, even if the original open connection hides its password.
        await using var connection = (SqlConnection)((ICloneable)Context.Database.GetDbConnection()).Clone();
        // Do not rebuild ConnectionString: an already-open source can hide its password,
        // whereas SqlConnection.Clone retains its private authentication state. Ambient
        // enlistment is suppressed above; opening is bounded by the overall cancellation token.

        // Retain the configured model (including custom schema), logging and interceptors,
        // but disable provider retries: this method owns the bounded fresh-context retry loop.
        var sourceOptions = Context.GetService<IDbContextOptions>();
        var options = new DbContextOptionsBuilder<AuditDbContext>(
                new DbContextOptions<AuditDbContext>(sourceOptions.Extensions.ToDictionary(static e => e.GetType())))
            .UseSqlServer(connection, static sql => sql.CommandTimeout(2)
                .ExecutionStrategy(static dependencies => new NonRetryingExecutionStrategy(dependencies)))
            .UseModel(Context.Model)
            .Options;
        await using var readContext = new AuditDbContext(options);
        await readContext.Database.OpenConnectionAsync(cancellationToken);
        // Bound lock waits below the per-command deadline so SQL can report a retryable 1222.
        await readContext.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 1500;", cancellationToken);
        await using var transaction = await readContext.Database.BeginTransactionAsync(
            IsolationLevel.Serializable, cancellationToken);
        var result = await ReadSequenceSummaryAsync(readContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private static async Task<bool> ReadSequenceSummaryAsync(AuditDbContext readContext,
        CancellationToken cancellationToken)
    {
        // One aggregate result instead of transferring the entire sequence column.
        // Distinct count also detects duplicates when the unique index is absent or damaged.
        var summary = await readContext.AuditIntegrity.AsNoTracking()
            .GroupBy(static ai => 1)
            .Select(static group => new
            {
                Count = group.LongCount(),
                DistinctCount = group.Select(static ai => ai.SequenceNumber).Distinct().LongCount(),
                Minimum = group.Min(static ai => ai.SequenceNumber),
                Maximum = group.Max(static ai => ai.SequenceNumber)
            })
            .SingleOrDefaultAsync(cancellationToken);

        // Decimal arithmetic keeps the range calculation safe across the full Int64 domain.
        return summary == null ||
               (summary.Count == summary.DistinctCount &&
                (decimal)summary.Maximum - summary.Minimum + 1 == summary.Count);
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
