using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Writes audit outbox rows to the consumer's database so they commit atomically with the
/// business write. The write path is chosen per save based on what the consumer context offers:
/// <list type="number">
/// <item><description>If the consumer's <c>DbContext</c> maps <see cref="AuditOutboxEntity"/>,
/// the row is staged on its change tracker and persisted by EF in the same
/// <c>SaveChangesAsync</c> unit of work (atomic via EF's implicit transaction; no explicit
/// transaction required). This is the Phase 06 design.</description></item>
/// <item><description>Otherwise, if an explicit <c>DbContext</c> transaction is active, the row
/// is inserted via parameterized raw SQL that enlists in that transaction. Duplicate idempotency
/// keys are handled as success.</description></item>
/// <item><description>Otherwise the write is rejected with <see cref="AuditOutboxAtomicityException"/>
/// — neither path can guarantee atomicity, and committing the audit row independently of the
/// business write would create false evidence.</description></item>
/// </list>
/// </summary>
internal sealed class AuditOutboxWriter(
    IConsumerDbContextAccessor accessor,
    IOptions<EntityFrameworkOptions> options,
    ILogger<AuditOutboxWriter> logger)
    : IAuditOutboxWriter
{
    /// <summary>
    /// Schema name for the outbox table, validated on startup to prevent SQL injection.
    /// </summary>
    private readonly string _schema = ValidateSchemaName(options.Value.Schema);

    private static string ValidateSchemaName(string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
            throw new ArgumentException("Schema name cannot be null or whitespace.", nameof(schema));

        if (schema.Length > 128)
            throw new ArgumentException($"Schema name exceeds maximum length of 128 characters: '{schema}'.", nameof(schema));

        foreach (var c in schema)
        {
            var isValid = char.IsLetterOrDigit(c) || c == '_';
            if (!isValid)
                throw new ArgumentException($"Schema name contains invalid character '{c}': '{schema}'. Only letters, digits, and underscores are allowed.", nameof(schema));
        }

        return schema;
    }

    public async Task<bool> WriteAsync(
        string envelopeJson,
        int envelopeVersion,
        Guid idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        var inserted = await WriteBatchAsync([(envelopeJson, envelopeVersion, idempotencyKey)], cancellationToken);
        return inserted > 0;
    }

    public async Task<int> WriteBatchAsync(
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return 0;

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxWrite,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.BatchSize, rows.Count);

        var consumerCtx = accessor.Current;

        // Hybrid atomicity policy — see the class summary. The outbox row must commit
        // atomically with the business write; how we get there depends on the consumer context.
        int written;
        string writeMode;
        if (consumerCtx.Model.FindEntityType(typeof(AuditOutboxEntity)) is { } outboxEntityType)
        {
            // (1) Mapped entity: stage on the change tracker; EF saves it in the same unit.
            ValidateMappedOutboxSchema(outboxEntityType.GetSchema());
            written = await AddViaChangeTrackerAsync(consumerCtx, rows, cancellationToken);
            writeMode = "change-tracker";
        }
        else if (consumerCtx.Database.CurrentTransaction is not null)
        {
            // (2) Unmapped but an explicit transaction is open: raw SQL enlists in it.
            written = await WriteViaRawSqlAsync(consumerCtx, rows, cancellationToken);
            writeMode = "raw-sql";
        }
        else
        {
            // (3) Neither: atomicity cannot be guaranteed — fail closed.
            activity?.SetTag(AuditActivitySource.Tags.Outcome, "rejected");
            throw new AuditOutboxAtomicityException(
                "AuditSinkMode.TransactionalOutbox cannot commit the audit outbox row atomically " +
                "with the business write: the consumer DbContext neither maps AuditOutboxEntity " +
                "(which would let EF persist it in the same SaveChangesAsync unit) nor has an active " +
                "DbContext transaction (which the raw-SQL outbox writer would enlist in). Map " +
                "AuditOutboxEntity in the consumer DbContext model, or open an explicit transaction " +
                "around the save.");
        }

        activity?.SetTag(AuditActivitySource.Tags.Outcome, "accepted");
        activity?.SetTag(AuditActivitySource.Tags.OutboxWriteMode, writeMode);
        activity?.SetTag(AuditActivitySource.Tags.OutboxRowsAccepted, written);
        activity?.SetTag(AuditActivitySource.Tags.OutboxDuplicates, rows.Count - written);

        return written;
    }

    /// <summary>
    /// (1) Stages outbox rows on the consumer context's change tracker. EF persists them in
    /// the same <c>SaveChangesAsync</c> unit as the business write — atomic without an explicit
    /// transaction. Duplicate idempotency keys are treated as success before save by checking
    /// already-tracked rows, duplicate keys inside the incoming batch, and persisted rows.
    /// </summary>
    private static async Task<int> AddViaChangeTrackerAsync(
        DbContext consumerCtx,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        CancellationToken cancellationToken)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var set = consumerCtx.Set<AuditOutboxEntity>();
        var candidateKeys = rows
            .Select(static r => r.idempotencyKey)
            .Distinct()
            .ToList();

        var duplicateKeys = consumerCtx.ChangeTracker
            .Entries<AuditOutboxEntity>()
            .Where(static e => e.State is not EntityState.Detached and not EntityState.Deleted)
            .Select(static e => e.Entity.IdempotencyKey)
            .ToHashSet();

        if (candidateKeys.Count > 0)
        {
            var persistedKeys = await set
                .AsNoTracking()
                .Where(e => candidateKeys.Contains(e.IdempotencyKey))
                .Select(static e => e.IdempotencyKey)
                .ToListAsync(cancellationToken);

            duplicateKeys.UnionWith(persistedKeys);
        }

        var stagedKeys = new HashSet<Guid>();
        var outboxRows = new List<AuditOutboxEntity>(rows.Count);

        foreach (var (envelopeJson, envelopeVersion, idempotencyKey) in rows)
        {
            if (duplicateKeys.Contains(idempotencyKey) || !stagedKeys.Add(idempotencyKey))
                continue;

            outboxRows.Add(CreateOutboxEntity(
                envelopeJson,
                envelopeVersion,
                idempotencyKey,
                createdAt));
        }

        if (outboxRows.Count > 0)
            set.AddRange(outboxRows);

        return outboxRows.Count;
    }

    private void ValidateMappedOutboxSchema(string? mappedSchema)
    {
        var effectiveSchema = string.IsNullOrWhiteSpace(mappedSchema) ? "dbo" : mappedSchema;
        if (!string.Equals(effectiveSchema, _schema, StringComparison.Ordinal))
        {
            throw new AuditOutboxAtomicityException(
                $"AuditSinkMode.TransactionalOutbox cannot use the mapped AuditOutboxEntity because " +
                $"it is mapped to schema '{effectiveSchema}' while the outbox drainer is configured " +
                $"for schema '{_schema}'. Configure EntityFrameworkOptions.Schema to match the " +
                "consumer DbContext mapping, or map AuditOutboxEntity to the configured audit schema.");
        }
    }

    private static AuditOutboxEntity CreateOutboxEntity(
        string envelopeJson,
        int envelopeVersion,
        Guid idempotencyKey,
        DateTimeOffset createdAt)
    {
        return new AuditOutboxEntity
            {
                EnvelopeJson = envelopeJson,
                EnvelopeVersion = envelopeVersion,
                CreatedAt = createdAt,
                IdempotencyKey = idempotencyKey,
            };
    }

    /// <summary>
    /// (2) Inserts outbox rows via parameterized raw SQL on the consumer connection, chunked to
    /// stay under SQL Server's 2100-parameter limit. Caller has already verified an ambient
    /// transaction is active so the inserts commit atomically with the business write.
    /// </summary>
    private async Task<int> WriteViaRawSqlAsync(
        DbContext consumerCtx,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        CancellationToken cancellationToken)
    {
        var totalInserted = 0;

        var chunks = Chunk(rows, QueryLimits.MaxOutboxBatchSize);
        foreach (var chunk in chunks)
        {
            totalInserted += await WriteChunkAsync(consumerCtx, chunk, cancellationToken);
        }

        return totalInserted;
    }

    private async Task<int> WriteChunkAsync(
        DbContext consumerCtx,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        CancellationToken cancellationToken)
    {
        var createdAt = DateTimeOffset.UtcNow;

        var parameters = new List<object>();
        var valuesClauses = new List<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var (envelopeJson, envelopeVersion, idempotencyKey) = rows[i];
            var id = Guid.NewGuid();
            var baseIndex = i * 7;

            valuesClauses.Add($"({{{baseIndex}}}, {{{baseIndex + 1}}}, {{{baseIndex + 2}}}, {{{baseIndex + 3}}}, {{{baseIndex + 4}}}, {{{baseIndex + 5}}}, {{{baseIndex + 6}}})");
            parameters.Add(id);
            parameters.Add(envelopeJson);
            parameters.Add(envelopeVersion);
            parameters.Add(0); // Status = Pending
            parameters.Add(createdAt);
            parameters.Add(0); // AttemptCount
            parameters.Add(idempotencyKey);
        }

        // Use INSERT with WHERE NOT EXISTS to handle duplicates gracefully.
        // This avoids exceptions on unique constraint violations while staying
        // within the consumer's transaction.
        var sql = $@"
INSERT INTO [{_schema}].[AuditOutbox]
    ([Id], [EnvelopeJson], [EnvelopeVersion], [Status], [CreatedAt], [AttemptCount], [IdempotencyKey])
SELECT v.[Id], v.[EnvelopeJson], v.[EnvelopeVersion], v.[Status], v.[CreatedAt], v.[AttemptCount], v.[IdempotencyKey]
FROM (VALUES {string.Join(", ", valuesClauses)}) AS v([Id], [EnvelopeJson], [EnvelopeVersion], [Status], [CreatedAt], [AttemptCount], [IdempotencyKey])
WHERE NOT EXISTS (
    SELECT 1 FROM [{_schema}].[AuditOutbox] o WHERE o.[IdempotencyKey] = v.[IdempotencyKey]
)";

        try
        {
            var inserted = await consumerCtx.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);

            if (inserted < rows.Count)
            {
                logger.LogDebug(
                    "Outbox write: {Inserted}/{Total} rows inserted, {Duplicates} duplicates skipped",
                    inserted, rows.Count, rows.Count - inserted);
            }

            return inserted;
        }
        catch (Exception ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // Race condition: another transaction inserted between our check and insert.
            // ExecuteSqlRawAsync throws provider exceptions directly (SqlException, etc.),
            // not DbUpdateException, so we catch the base Exception type.
            logger.LogDebug(ex, "Duplicate key conflict in batch insert, falling back to individual inserts");
            return await WriteIndividuallyAsync(consumerCtx, rows, createdAt, cancellationToken);
        }
    }

    private async Task<int> WriteIndividuallyAsync(
        DbContext consumerCtx,
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var inserted = 0;

        foreach (var (envelopeJson, envelopeVersion, idempotencyKey) in rows)
        {
            var id = Guid.NewGuid();
            var sql = $@"
INSERT INTO [{_schema}].[AuditOutbox]
    ([Id], [EnvelopeJson], [EnvelopeVersion], [Status], [CreatedAt], [AttemptCount], [IdempotencyKey])
SELECT {{0}}, {{1}}, {{2}}, {{3}}, {{4}}, {{5}}, {{6}}
WHERE NOT EXISTS (
    SELECT 1 FROM [{_schema}].[AuditOutbox] o WHERE o.[IdempotencyKey] = {{6}}
)";

            try
            {
                var result = await consumerCtx.Database.ExecuteSqlRawAsync(
                    sql,
                    [id, envelopeJson, envelopeVersion, 0, createdAt, 0, idempotencyKey],
                    cancellationToken);

                if (result > 0)
                    inserted++;
            }
            catch (Exception ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
            {
                // ExecuteSqlRawAsync throws provider exceptions directly (SqlException, etc.),
                // not DbUpdateException, so we catch the base Exception type.
                logger.LogDebug("Duplicate outbox row skipped for IdempotencyKey {Key}", idempotencyKey);
            }
        }

        return inserted;
    }

    private static IEnumerable<IReadOnlyList<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
        {
            var count = Math.Min(size, source.Count - i);
            var chunk = new T[count];
            for (var j = 0; j < count; j++)
                chunk[j] = source[i + j];
            yield return chunk;
        }
    }
}
