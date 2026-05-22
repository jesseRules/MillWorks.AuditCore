using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// <see cref="IAuditSink"/> implementation that writes audit envelopes to a
/// transactional outbox table inside the consumer's transaction. A background
/// <c>AuditOutboxDrainer</c> reads pending rows and publishes them through
/// <see cref="ImmediateSink"/> to the audit DbContext.
/// </summary>
/// <remarks>
/// This sink is used when <c>SecurityOptions.AuditSinkMode</c> is set to
/// <c>TransactionalOutbox</c>. It provides atomic commit of business + audit
/// data for regulated/zero-loss-durability deployments.
/// </remarks>
internal sealed class TransactionalOutboxSink(
    IAuditOutboxWriter outboxWriter,
    ILogger<TransactionalOutboxSink> logger)
    : IAuditSink
{
    /// <summary>
    /// Current envelope serialization format version. Increment when the
    /// <see cref="AuditEnvelope"/> schema changes incompatibly to allow the
    /// drainer to detect version skew.
    /// </summary>
    internal const int CurrentEnvelopeVersion = 1;

    /// <summary>
    /// JSON serialization options for envelopes. Uses camelCase naming to minimize storage size.
    /// </summary>
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Publishes an audit envelope by writing it to the outbox table with an idempotency key.
    /// </summary>
    /// <param name="envelope"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        var idempotencyKey = ExtractIdempotencyKey(envelope);
        var inserted = await outboxWriter.WriteAsync(json, CurrentEnvelopeVersion, idempotencyKey, cancellationToken);

        if (inserted)
        {
            logger.LogDebug(
                "Wrote outbox row for {Kind} envelope, entity {EntityName}",
                envelope.Kind,
                envelope.EntityName);
        }
        else
        {
            logger.LogDebug(
                "Duplicate outbox row skipped for {Kind} envelope, entity {EntityName}, IdempotencyKey {Key}",
                envelope.Kind,
                envelope.EntityName,
                idempotencyKey);
        }
    }

    /// <summary>
    /// Publishes a batch of audit envelopes by writing them to the outbox table with their respective idempotency keys.
    /// </summary>
    /// <param name="envelopes"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ArgumentNullException"></exception>
    public async Task PublishBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count == 0)
            return;

        var rows = new List<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)>(envelopes.Count);
        rows.AddRange(from envelope in envelopes
            let json = JsonSerializer.Serialize(envelope, _jsonOptions)
            let idempotencyKey = ExtractIdempotencyKey(envelope)
            select (json, CurrentEnvelopeVersion, idempotencyKey));

        var inserted = await outboxWriter.WriteBatchAsync(rows, cancellationToken);

        logger.LogDebug(
            "Wrote {Inserted}/{Total} outbox row(s) in batch ({Duplicates} duplicates)",
            inserted,
            rows.Count,
            rows.Count - inserted);
    }

    /// <summary>
    /// Extracts the idempotency key from an envelope based on its kind.
    /// <list type="bullet">
    /// <item><description>ExplicitEvent: Uses <see cref="AuditEnvelope.ExplicitEventId"/> (the original
    /// AuditEvent.EventId) to prevent duplicate logical events from producing duplicate outbox rows.
    /// Falls back to EnvelopeId if ExplicitEventId is not set (backwards compatibility).</description></item>
    /// <item><description>EntityChange: Uses the envelope's EnvelopeId (stable envelope identity).</description></item>
    /// </list>
    /// </summary>
    internal static Guid ExtractIdempotencyKey(AuditEnvelope envelope)
    {
        if (envelope is { Kind: AuditEnvelopeKind.ExplicitEvent, ExplicitEventId: not null })
            return envelope.ExplicitEventId.Value;

        return envelope.EnvelopeId;
    }
}
