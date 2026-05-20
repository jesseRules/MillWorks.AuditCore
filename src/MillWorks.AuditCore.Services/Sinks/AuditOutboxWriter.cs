using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Writes audit outbox rows to the consumer's database via parameterized raw SQL.
/// The row is inserted into the consumer's transaction so it commits atomically
/// with the business write. Duplicate idempotency keys are handled as success.
/// </summary>
internal sealed class AuditOutboxWriter : IAuditOutboxWriter
{
    private readonly IConsumerDbContextAccessor _accessor;
    private readonly ILogger<AuditOutboxWriter> _logger;
    private readonly string _schema;

    public AuditOutboxWriter(
        IConsumerDbContextAccessor accessor,
        IOptions<EntityFrameworkOptions> options,
        ILogger<AuditOutboxWriter> logger)
    {
        _accessor = accessor;
        _logger = logger;
        _schema = options.Value.Schema;
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

        var totalInserted = 0;

        // Chunk to stay under SQL Server's 2100 parameter limit (7 params per row)
        var chunks = Chunk(rows, QueryLimits.MaxOutboxBatchSize);
        foreach (var chunk in chunks)
        {
            totalInserted += await WriteChunkAsync(chunk, cancellationToken);
        }

        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");
        activity?.SetTag("audit.outbox.inserted", totalInserted);
        activity?.SetTag("audit.outbox.duplicates", rows.Count - totalInserted);

        return totalInserted;
    }

    private async Task<int> WriteChunkAsync(
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        CancellationToken cancellationToken)
    {
        var consumerCtx = _accessor.Current;
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
                _logger.LogDebug(
                    "Outbox write: {Inserted}/{Total} rows inserted, {Duplicates} duplicates skipped",
                    inserted, rows.Count, rows.Count - inserted);
            }

            return inserted;
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // Race condition: another transaction inserted between our check and insert.
            // Fall back to one-at-a-time to determine which rows are duplicates.
            _logger.LogDebug(ex, "Duplicate key conflict in batch insert, falling back to individual inserts");
            return await WriteIndividuallyAsync(rows, createdAt, cancellationToken);
        }
    }

    private async Task<int> WriteIndividuallyAsync(
        IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var consumerCtx = _accessor.Current;
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
            catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
            {
                _logger.LogDebug("Duplicate outbox row skipped for IdempotencyKey {Key}", idempotencyKey);
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
