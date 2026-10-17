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
internal sealed partial class AuditOutboxWriter(
    IConsumerDbContextAccessor accessor,
    IOptions<EntityFrameworkOptions> options,
    ILogger<AuditOutboxWriter> logger)
    : IAuditIdentityAwareOutboxWriter
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
            throw new ArgumentException($"Schema name exceeds maximum length of 128 characters: '{schema}'.",
                nameof(schema));

        foreach (var c in schema)
        {
            var isValid = char.IsLetterOrDigit(c) || c == '_';
            if (!isValid)
                throw new ArgumentException(
                    $"Schema name contains invalid character '{c}': '{schema}'. Only letters, digits, and underscores are allowed.",
                    nameof(schema));
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

        var envelopes = rows.Select(r => Recovery.QueuedEnvelopeValidator.Read(r.envelopeJson, r.envelopeVersion)).ToList();
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].idempotencyKey != Identity.AuditIdentityLedger.LogicalId(envelopes[i]))
                throw new ArgumentException("Outbox idempotency key must match logical envelope identity.", nameof(rows));
        // Same-key content must agree before any source row or receipt can be staged.
        foreach (var group in envelopes.GroupBy(Identity.AuditIdentityLedger.LogicalId))
            if (group.Select(Identity.AuditEnvelopeContent.Fingerprint).Distinct(StringComparer.Ordinal).Count() > 1)
                throw new Identity.AuditIdentityConflictException([group.Key]);
        var consumerCtx = accessor.Current;

        // Hybrid atomicity policy — see the class summary. The outbox row must commit
        // atomically with the business write; how we get there depends on the consumer context.
        int written;
        string writeMode;
        if (consumerCtx.Model.FindEntityType(typeof(AuditOutboxEntity)) is { } outboxEntityType)
        {
            // (1) Mapped entity: stage on the change tracker; EF saves it in the same unit.
            ValidateMappedOutboxSchema(outboxEntityType.GetSchema());
            var identityType = consumerCtx.Model.FindEntityType(typeof(AuditEnvelopeIdentity));
            if (identityType is null || identityType.GetTableName() != "AuditEnvelopeIdentities" ||
                identityType.GetSchema() != outboxEntityType.GetSchema() || outboxEntityType.GetTableName() != "AuditOutbox")
                throw new AuditOutboxAtomicityException("Mapped consumers must map both AuditOutbox and AuditEnvelopeIdentities in the configured audit schema.");
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

        logger.LogDebug("Accepted {AcceptedCount} new outbox identity row(s)", written);
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
        var envelopes = rows.Select(r => Recovery.QueuedEnvelopeValidator.Read(r.envelopeJson, r.envelopeVersion)).ToList();
        var durable = await Identity.AuditIdentityLedger.StageAsync(consumerCtx, "outbox", envelopes, cancellationToken);
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

        duplicateKeys.UnionWith(durable);
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

}
