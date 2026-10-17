using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Default <see cref="IAuditSink"/>: persists each envelope synchronously on
/// publish. Routes envelopes by <see cref="AuditEnvelopeKind"/> to the appropriate
/// batch writer, then combines outcomes internally. Writers commit independently; a later
/// failure or cancellation does not roll back earlier audit writes.
/// </summary>
internal sealed class ImmediateSink(
    IAuditEntityBatchWriter entityBatchWriter,
    IAuditEventBatchWriter eventBatchWriter,
    ILogger<ImmediateSink> logger,
    IAuditEnvelopeWriteAuthorization? authorization = null) : IAuditSink
{
    /// <inheritdoc />
    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await PublishBatchAsync([envelope], cancellationToken);
    }

    /// <inheritdoc />
    public async Task PublishBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken = default)
    {
        Identity.AuditEnvelopeContent.ValidateBatch(envelopes);
        foreach (var envelope in envelopes) _ = Identity.AuditEnvelopeContent.Fingerprint(envelope);
        if (envelopes.Count == 0)
            return;

        await using var permit = await AuditEnvelopeWriteAuthority.AcquireAsync(authorization, envelopes, cancellationToken);
        var entityChanges = new List<AuditEnvelope>();
        var explicitEvents = new List<AuditEnvelope>();

        for (var index = 0; index < envelopes.Count; index++)
        {
            var envelope = envelopes[index];
            if (envelope is null)
                throw new ArgumentException($"Batch entry at index {index} must not be null.", nameof(envelopes));

            switch (envelope.Kind)
            {
                case AuditEnvelopeKind.EntityChange:
                    entityChanges.Add(envelope);
                    break;

                case AuditEnvelopeKind.ExplicitEvent:
                    explicitEvents.Add(envelope);
                    break;

                default:
                    logger.LogError("Unknown AuditEnvelopeKind {Kind}", envelope.Kind);
                    throw new InvalidOperationException(
                        $"Unhandled AuditEnvelopeKind: {envelope.Kind}");
            }
        }

        var entityOutcomes = entityChanges.Count > 0
            ? await entityBatchWriter.WriteBatchAsync(entityChanges, cancellationToken)
            : [];

        var eventOutcomes = explicitEvents.Count > 0
            ? await eventBatchWriter.WriteBatchAsync(explicitEvents, cancellationToken)
            : [];

        WriteOutcomeValidation.Validate(entityOutcomes, entityChanges.Select(e => e.EnvelopeId));
        WriteOutcomeValidation.Validate(eventOutcomes, explicitEvents.Select(e => e.EnvelopeId));
        await permit.CheckCurrentAsync(cancellationToken);
        var allOutcomes = CombineOutcomes(entityOutcomes, eventOutcomes);

        var failedOutcomes = allOutcomes.Where(static o => !o.Succeeded).ToList();
        if (failedOutcomes.Count > 0)
        {
            var firstFailed = failedOutcomes[0];
            var failedIds = failedOutcomes.Select(static o => o.EnvelopeId).ToList();

            logger.LogError(
                "ImmediateSink write failed: {FailedCount}/{TotalCount} envelope(s) failed. First error: {FirstError}",
                failedOutcomes.Count, allOutcomes.Count, firstFailed.ErrorMessage);

            var byId = envelopes.ToDictionary(static e => e.EnvelopeId);
            foreach (var failed in failedOutcomes.Take(20))
            {
                var input = byId[failed.EnvelopeId];
                var reason = failed.Exception is Identity.AuditIdentityConflictException identity
                    ? identity.Message : "audit_write_failed";
                logger.LogError("Audit publication failed for envelope {EnvelopeId}, logical ID {LogicalId}, " +
                    "entity {EntityName}, action {Action}, reason {ErrorCode}", failed.EnvelopeId,
                    input.Kind == AuditEnvelopeKind.ExplicitEvent ? input.ExplicitEventId : input.EnvelopeId,
                    input.EntityName, input.Action, reason);
            }

            AuditEnvelopeKind? kind = null;
            if (entityChanges.Count > 0 && explicitEvents.Count == 0)
                kind = AuditEnvelopeKind.EntityChange;
            else if (explicitEvents.Count > 0 && entityChanges.Count == 0)
                kind = AuditEnvelopeKind.ExplicitEvent;

            throw new AuditWriteException(
                allOutcomes.Count,
                failedOutcomes.Count,
                failedIds,
                kind,
                firstFailed.ErrorMessage ?? "Unknown error",
                firstFailed.Exception);
        }
    }

    /// <summary>
    /// Combines write outcomes from entity and event batch writers into a single list for unified error handling.
    /// Since the two writers are independent, we simply concatenate their results.
    /// In cases where one writer is not used (e.g., no entity changes), we return the outcomes from the other writer directly to avoid unnecessary list allocation.
    /// </summary>
    /// <param name="entityOutcomes"></param>
    /// <param name="eventOutcomes"></param>
    /// <returns></returns>
    private static IReadOnlyList<WriteOutcome> CombineOutcomes(
        IReadOnlyList<WriteOutcome> entityOutcomes,
        IReadOnlyList<WriteOutcome> eventOutcomes)
    {
        if (entityOutcomes.Count == 0)
            return eventOutcomes;
        if (eventOutcomes.Count == 0)
            return entityOutcomes;

        var combined = new List<WriteOutcome>(entityOutcomes.Count + eventOutcomes.Count);
        combined.AddRange(entityOutcomes);
        combined.AddRange(eventOutcomes);
        return combined;
    }
}
