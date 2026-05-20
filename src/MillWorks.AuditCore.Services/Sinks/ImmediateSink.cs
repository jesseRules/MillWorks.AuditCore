using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Default <see cref="IAuditSink"/>: persists each envelope synchronously on
/// publish. Routes envelopes by <see cref="AuditEnvelopeKind"/> to the appropriate
/// batch writer, then combines outcomes internally.
/// </summary>
internal sealed class ImmediateSink(
    IAuditEntityBatchWriter entityBatchWriter,
    IAuditEventBatchWriter eventBatchWriter,
    ILogger<ImmediateSink> logger) : IAuditSink
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
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count == 0)
            return;

        var entityChanges = new List<AuditEnvelope>();
        var explicitEvents = new List<AuditEnvelope>();

        foreach (var envelope in envelopes)
        {
            if (envelope is null)
                continue;

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

        var allOutcomes = CombineOutcomes(entityOutcomes, eventOutcomes);

        var failedCount = allOutcomes.Count(static o => !o.Succeeded);
        if (failedCount > 0)
        {
            var firstError = allOutcomes.FirstOrDefault(static o => !o.Succeeded);
            logger.LogWarning(
                "Batch publish had {FailedCount} failure(s) of {TotalCount}: {FirstError}",
                failedCount, allOutcomes.Count, firstError?.ErrorMessage);
        }
    }

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
