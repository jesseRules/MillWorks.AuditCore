using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Writes audit outbox rows to the consumer's database via parameterized raw SQL.
/// The row is inserted into the consumer's transaction so it commits atomically
/// with the business write.
/// </summary>
internal sealed class AuditOutboxWriter : IAuditOutboxWriter
{
    private readonly IConsumerDbContextAccessor _accessor;
    private readonly string _schema;

    public AuditOutboxWriter(
        IConsumerDbContextAccessor accessor,
        IOptions<EntityFrameworkOptions> options)
    {
        _accessor = accessor;
        _schema = options.Value.Schema;
    }

    public async Task WriteAsync(
        string envelopeJson,
        int envelopeVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopeJson);
        await WriteBatchAsync([(envelopeJson, envelopeVersion)], cancellationToken);
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<(string envelopeJson, int envelopeVersion)> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return;

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxWrite,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.BatchSize, rows.Count);

        // Chunk to stay under SQL Server's 2100 parameter limit (6 params per row)
        var chunks = Chunk(rows, QueryLimits.MaxOutboxBatchSize);
        foreach (var chunk in chunks)
        {
            await WriteChunkAsync(chunk, cancellationToken);
        }

        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");
    }

    private async Task WriteChunkAsync(
        IReadOnlyList<(string envelopeJson, int envelopeVersion)> rows,
        CancellationToken cancellationToken)
    {
        var consumerCtx = _accessor.Current;
        var createdAt = DateTimeOffset.UtcNow;

        var parameters = new List<object>();
        var valuesClauses = new List<string>();

        for (var i = 0; i < rows.Count; i++)
        {
            var (envelopeJson, envelopeVersion) = rows[i];
            var id = Guid.NewGuid();
            var baseIndex = i * 6;

            valuesClauses.Add($"({{{baseIndex}}}, {{{baseIndex + 1}}}, {{{baseIndex + 2}}}, {{{baseIndex + 3}}}, {{{baseIndex + 4}}}, {{{baseIndex + 5}}})");
            parameters.Add(id);
            parameters.Add(envelopeJson);
            parameters.Add(envelopeVersion);
            parameters.Add(0); // Status = Pending
            parameters.Add(createdAt);
            parameters.Add(0); // AttemptCount
        }

        var sql = $"INSERT INTO [{_schema}].[AuditOutbox] " +
            "([Id], [EnvelopeJson], [EnvelopeVersion], [Status], [CreatedAt], [AttemptCount]) VALUES " +
            string.Join(", ", valuesClauses);

        await consumerCtx.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
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
