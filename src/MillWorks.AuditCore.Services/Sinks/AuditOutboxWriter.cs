using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;

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

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxWrite,
            ActivityKind.Internal);

        var consumerCtx = _accessor.Current;
        var id = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        activity?.SetTag(AuditActivitySource.Tags.OutboxRowId, id.ToString());

        var sql = $"INSERT INTO [{_schema}].[AuditOutbox] " +
            "([Id], [EnvelopeJson], [EnvelopeVersion], [Status], [CreatedAt], [AttemptCount]) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})";

        await consumerCtx.Database.ExecuteSqlRawAsync(
            sql,
            [id, envelopeJson, envelopeVersion, 0, createdAt, 0],
            cancellationToken);

        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");
    }
}
