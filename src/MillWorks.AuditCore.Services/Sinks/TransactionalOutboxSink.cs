using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// <see cref="IAuditSink"/> implementation that writes audit envelopes to a
/// transactional outbox table inside the consumer's transaction. A background
/// <c>AuditOutboxDrainer</c> reads committed pending rows and processes them through
/// the audit batch writers.
/// </summary>
/// <remarks>
/// This sink is used when <c>SecurityOptions.AuditSinkMode</c> is set to
/// <c>TransactionalOutbox</c>. It provides atomic commit of business + audit
/// data when the consumer commits that transaction. Successful publication does not commit
/// the consumer transaction or guarantee that the audit record is already queryable.
/// </remarks>
internal sealed class TransactionalOutboxSink(
    IAuditOutboxWriter outboxWriter,
    ILogger<TransactionalOutboxSink> logger,
    IAuditEnvelopeWriteAuthorization? authorization = null)
    : IAuditSink
{
    /// <summary>
    /// Current envelope serialization format version. Increment when the
    /// <see cref="AuditEnvelope"/> schema changes incompatibly to allow the
    /// drainer to detect version skew.
    /// </summary>
    internal const int CurrentEnvelopeVersion = 2;

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

        var json = SerializeValidated(envelope);
        await using var permit = await AuditEnvelopeWriteAuthority.AcquireAsync(authorization, [envelope], cancellationToken);
        var idempotencyKey = ExtractIdempotencyKey(envelope);
        EnsureIdentityProtocol();
        var inserted = await outboxWriter.WriteAsync(json, CurrentEnvelopeVersion, idempotencyKey, cancellationToken);

        await permit.CheckCurrentAsync(cancellationToken);
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
    /// Validates and serializes the entire batch before passing rows to the consumer outbox
    /// writer. Successful return is acceptance inside the consumer transaction, which may
    /// still roll back. It is not final audit persistence.
    /// </summary>
    /// <param name="envelopes"></param>
    /// <param name="cancellationToken"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException">A batch entry is null; no rows are passed to the outbox writer.</exception>
    public async Task PublishBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        Identity.AuditEnvelopeContent.ValidateBatch(envelopes);
        foreach (var envelope in envelopes) _ = Identity.AuditEnvelopeContent.Fingerprint(envelope);
        if (envelopes.Count == 0)
            return;

        var rows = new List<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)>(envelopes.Count);
        for (var index = 0; index < envelopes.Count; index++)
        {
            var envelope = envelopes[index];
            if (envelope is null)
                throw new ArgumentException($"Batch entry at index {index} must not be null.", nameof(envelopes));

            rows.Add((SerializeValidated(envelope), CurrentEnvelopeVersion, ExtractIdempotencyKey(envelope)));
        }

        await using var permit = await AuditEnvelopeWriteAuthority.AcquireAsync(authorization, envelopes, cancellationToken);
        EnsureIdentityProtocol();
        var inserted = await outboxWriter.WriteBatchAsync(rows, cancellationToken);
        await permit.CheckCurrentAsync(cancellationToken);

        logger.LogDebug(
            "Wrote {Inserted}/{Total} outbox row(s) in batch ({Duplicates} duplicates)",
            inserted,
            rows.Count,
            rows.Count - inserted);
    }

    private void EnsureIdentityProtocol()
    {
        if (outboxWriter is not IAuditIdentityAwareOutboxWriter)
            throw new InvalidOperationException("The configured outbox writer must implement IAuditIdentityAwareOutboxWriter.");
    }

    private static string SerializeValidated(AuditEnvelope envelope)
    {
        Identity.AuditEnvelopeContent.Validate(envelope);
        _ = Identity.AuditEnvelopeContent.Fingerprint(envelope);
        var json = JsonSerializer.Serialize(envelope, _jsonOptions);
        Recovery.QueuedEnvelopeValidator.Read(json, CurrentEnvelopeVersion);
        return json;
    }

    /// <summary>
    /// Extracts the idempotency key from an envelope based on its kind.
    /// <list type="bullet">
    /// <item><description>ExplicitEvent: Uses <see cref="AuditEnvelope.ExplicitEventId"/> (the original
    /// AuditEvent.EventId) to prevent duplicate logical events from producing duplicate outbox rows.
    /// Missing/empty logical IDs are rejected before persistence.</description></item>
    /// <item><description>EntityChange: Uses the envelope's EnvelopeId (stable envelope identity).</description></item>
    /// </list>
    /// </summary>
    internal static Guid ExtractIdempotencyKey(AuditEnvelope envelope)
    {
        Identity.AuditEnvelopeContent.Validate(envelope);
        return Identity.AuditIdentityLedger.LogicalId(envelope);
    }
}
