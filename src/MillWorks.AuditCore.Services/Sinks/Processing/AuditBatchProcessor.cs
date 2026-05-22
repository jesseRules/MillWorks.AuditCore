using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Services.Sinks.Processing;

/// <summary>
/// Processes claimed outbox rows by routing to appropriate batch writers based on
/// envelope kind. Maps write outcomes back to row outcomes for drainer status decisions.
/// </summary>
internal sealed class AuditBatchProcessor(
    IAuditEntityBatchWriter entityBatchWriter,
    IAuditEventBatchWriter eventBatchWriter,
    TimeProvider timeProvider,
    ILogger<AuditBatchProcessor> logger) : IAuditBatchProcessor
{
    /// <inheritdoc />
    public async Task<BatchProcessingResult> ProcessBatchAsync(
        IReadOnlyList<ClaimedOutboxRow> rows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0)
            return BatchProcessingResult.Empty;

        var now = timeProvider.GetUtcNow();

        var entityChangeRows = new List<ClaimedOutboxRow>();
        var explicitEventRows = new List<ClaimedOutboxRow>();
        var outcomes = new List<RowOutcome>(rows.Count);

        foreach (var row in rows)
        {
            RecordRowAge(row, now);

            switch (row.Envelope.Kind)
            {
                case AuditEnvelopeKind.EntityChange:
                    entityChangeRows.Add(row);
                    break;

                case AuditEnvelopeKind.ExplicitEvent:
                    explicitEventRows.Add(row);
                    break;

                default:
                    logger.LogError(
                        "Unknown AuditEnvelopeKind {Kind} for row {RowId}",
                        row.Envelope.Kind, row.RowId);
                    outcomes.Add(RowOutcome.Failed(
                        row.RowId,
                        $"Unhandled AuditEnvelopeKind: {row.Envelope.Kind}"));
                    break;
            }
        }

        RecordBatchSizeMetrics(entityChangeRows.Count, explicitEventRows.Count);

        var entityOutcomes = await ProcessEnvelopesAsync(
            entityChangeRows,
            entityBatchWriter.WriteBatchAsync,
            "Entity",
            cancellationToken);
        outcomes.AddRange(entityOutcomes);

        var eventOutcomes = await ProcessEnvelopesAsync(
            explicitEventRows,
            eventBatchWriter.WriteBatchAsync,
            "Event",
            cancellationToken);
        outcomes.AddRange(eventOutcomes);

        RecordOutcomeMetrics(outcomes, rows);

        return new BatchProcessingResult { Outcomes = outcomes };
    }

    private async Task<IReadOnlyList<RowOutcome>> ProcessEnvelopesAsync(
        List<ClaimedOutboxRow> rows,
        Func<IReadOnlyList<AuditEnvelope>, CancellationToken, Task<IReadOnlyList<WriteOutcome>>> writeBatchAsync,
        string writerName,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
            return [];

        var envelopes = rows.Select(static r => r.Envelope).ToList();
        var envelopeIdToRow = rows.ToDictionary(
            static r => r.Envelope.EnvelopeId,
            static r => r);

        IReadOnlyList<WriteOutcome> writeOutcomes;
        try
        {
            writeOutcomes = await writeBatchAsync(envelopes, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "{WriterName} batch writer threw for {Count} envelopes, marking all as retryable",
                writerName, envelopes.Count);
            return rows.Select(r => RowOutcome.Retry(r.RowId, ex.Message, exception: ex)).ToList();
        }

        return MapWriteOutcomesToRowOutcomes(writeOutcomes, envelopeIdToRow);
    }

    private static string GetEnvelopeKindTagValue(AuditEnvelopeKind kind) => kind switch
    {
        AuditEnvelopeKind.EntityChange => "entity_change",
        AuditEnvelopeKind.ExplicitEvent => "explicit_event",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static void RecordRowAge(ClaimedOutboxRow row, DateTimeOffset now)
    {
        var age = (now - row.CreatedAt).TotalSeconds;
        if (age >= 0)
        {
            var kindTag = new KeyValuePair<string, object?>(
                AuditMetrics.Tags.EnvelopeKind, GetEnvelopeKindTagValue(row.Envelope.Kind));
            AuditMetrics.OutboxRowAge.Record(age, kindTag);
        }
    }

    private static void RecordBatchSizeMetrics(int entityCount, int eventCount)
    {
        if (entityCount > 0)
        {
            AuditMetrics.OutboxBatchSize.Record(entityCount,
                new KeyValuePair<string, object?>(AuditMetrics.Tags.EnvelopeKind, "entity_change"));
        }
        if (eventCount > 0)
        {
            AuditMetrics.OutboxBatchSize.Record(eventCount,
                new KeyValuePair<string, object?>(AuditMetrics.Tags.EnvelopeKind, "explicit_event"));
        }
    }

    private static void RecordOutcomeMetrics(
        List<RowOutcome> outcomes,
        IReadOnlyList<ClaimedOutboxRow> rows)
    {
        var rowLookup = rows.ToDictionary(static r => r.RowId);

        foreach (var outcome in outcomes)
        {
            if (!rowLookup.TryGetValue(outcome.RowId, out var row))
                continue;

            var kindTag = new KeyValuePair<string, object?>(
                AuditMetrics.Tags.EnvelopeKind, GetEnvelopeKindTagValue(row.Envelope.Kind));

            switch (outcome.Status)
            {
                case RowStatus.Succeeded:
                    AuditMetrics.EnvelopesPublished.Add(1, kindTag);
                    break;

                case RowStatus.Duplicate:
                    AuditMetrics.EnvelopesDuplicate.Add(1, kindTag);
                    break;

                case RowStatus.Failed:
                    var failErrorType = AuditMetrics.ClassifyError(outcome.Exception);
                    AuditMetrics.EnvelopesFailed.Add(1, kindTag,
                        new KeyValuePair<string, object?>(AuditMetrics.Tags.ErrorType, failErrorType));
                    break;

                case RowStatus.RetryLater:
                    var retryErrorType = AuditMetrics.ClassifyError(outcome.Exception);
                    AuditMetrics.RetryAttempts.Add(1, kindTag,
                        new KeyValuePair<string, object?>(AuditMetrics.Tags.ErrorType, retryErrorType));
                    break;
            }
        }
    }

    private IReadOnlyList<RowOutcome> MapWriteOutcomesToRowOutcomes(
        IReadOnlyList<WriteOutcome> writeOutcomes,
        Dictionary<Guid, ClaimedOutboxRow> envelopeIdToRow)
    {
        var rowOutcomes = new List<RowOutcome>(envelopeIdToRow.Count);
        var coveredEnvelopeIds = new HashSet<Guid>();

        foreach (var wo in writeOutcomes)
        {
            if (!envelopeIdToRow.TryGetValue(wo.EnvelopeId, out var row))
            {
                logger.LogError(
                    "WriteOutcome for unknown EnvelopeId {EnvelopeId}; cannot map to row",
                    wo.EnvelopeId);
                continue;
            }

            coveredEnvelopeIds.Add(wo.EnvelopeId);
            var rowOutcome = MapSingleOutcome(wo, row.RowId);
            rowOutcomes.Add(rowOutcome);
        }

        if (coveredEnvelopeIds.Count < envelopeIdToRow.Count)
        {
            var missingCount = envelopeIdToRow.Count - coveredEnvelopeIds.Count;
            logger.LogError(
                "Writer returned {ReturnedCount}/{ExpectedCount} outcomes; {MissingCount} row(s) missing. " +
                "Failing missing rows to avoid InFlight limbo.",
                writeOutcomes.Count, envelopeIdToRow.Count, missingCount);

            foreach (var (envelopeId, row) in envelopeIdToRow)
            {
                if (!coveredEnvelopeIds.Contains(envelopeId))
                {
                    rowOutcomes.Add(RowOutcome.Failed(
                        row.RowId,
                        $"Writer did not return outcome for EnvelopeId {envelopeId}"));
                }
            }
        }

        return rowOutcomes;
    }

    private static RowOutcome MapSingleOutcome(WriteOutcome wo, Guid rowId)
    {
        if (wo.Succeeded)
        {
            return wo.IsDuplicate
                ? RowOutcome.Duplicate(rowId)
                : RowOutcome.Success(rowId);
        }

        return wo.IsRetryable
            ? RowOutcome.Retry(rowId, wo.ErrorMessage ?? "Unknown error", exception: wo.Exception)
            : RowOutcome.Failed(rowId, wo.ErrorMessage ?? "Unknown error", wo.Exception);
    }
}
