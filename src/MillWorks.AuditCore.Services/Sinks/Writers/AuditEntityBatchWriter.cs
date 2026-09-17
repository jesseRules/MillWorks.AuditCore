using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Sinks.Writers;

/// <summary>
/// Batch writer for entity-change envelopes. Maps envelopes to <see cref="AuditLogEntity"/> rows
/// and persists them in a single database transaction, returning per-envelope outcomes.
/// </summary>
internal sealed class AuditEntityBatchWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditEntityBatchWriter> logger) : IAuditEntityBatchWriter
{
    public async Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);

        if (envelopes.Count == 0)
            return [];

        var outcomes = new List<WriteOutcome>(envelopes.Count);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var auditLogSet = dbContext.Set<AuditLogEntity>();
            var envelopeIds = envelopes
                .Where(static envelope => envelope is not null)
                .Select(static envelope => envelope.EnvelopeId)
                .Distinct()
                .ToList();
            var persistedKeys = await auditLogSet
                .AsNoTracking()
                .Where(row => row.EnvelopeId.HasValue && envelopeIds.Contains(row.EnvelopeId.Value))
                .Select(static row => new { EnvelopeId = row.EnvelopeId!.Value, row.PropertyName })
                .ToListAsync(cancellationToken);
            var knownKeys = persistedKeys
                .Select(static row => (row.EnvelopeId, row.PropertyName))
                .ToHashSet();
            var totalRows = 0;

            foreach (var envelope in envelopes)
            {
                if (envelope is null)
                    continue;

                var addedRows = 0;
                var changes = envelope.PropertyChanges;
                if (changes is { Count: > 0 })
                {
                    foreach (var change in changes)
                    {
                        if (!knownKeys.Add((envelope.EnvelopeId, change.PropertyName)))
                            continue;

                        auditLogSet.Add(new AuditLogEntity
                        {
                            EnvelopeId = envelope.EnvelopeId,
                            EntityName = envelope.EntityName,
                            EntityId = envelope.EntityId,
                            Action = envelope.Action,
                            PropertyName = change.PropertyName,
                            OldValue = change.OldValue,
                            NewValue = change.NewValue,
                            Description = envelope.Description,
                            AdditionalData = envelope.AdditionalData,
                            CorrelationId = envelope.CorrelationId,
                            IpAddress = envelope.IpAddress,
                            UserAgent = envelope.UserAgent
                        });
                        totalRows++;
                        addedRows++;
                    }
                }
                else
                {
                    if (knownKeys.Add((envelope.EnvelopeId, null)))
                    {
                        auditLogSet.Add(new AuditLogEntity
                        {
                            EnvelopeId = envelope.EnvelopeId,
                            EntityName = envelope.EntityName,
                            EntityId = envelope.EntityId,
                            Action = envelope.Action,
                            Description = envelope.Description,
                            AdditionalData = envelope.AdditionalData,
                            CorrelationId = envelope.CorrelationId,
                            IpAddress = envelope.IpAddress,
                            UserAgent = envelope.UserAgent
                        });
                        totalRows++;
                        addedRows++;
                    }
                }

                outcomes.Add(addedRows == 0
                    ? WriteOutcome.Duplicate(envelope.EnvelopeId)
                    : WriteOutcome.Success(envelope.EnvelopeId));
            }

            var written = totalRows == 0
                ? 0
                : await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug(
                "Wrote {RowCount} AuditLog row(s) for {EnvelopeCount} envelope(s)",
                written, envelopes.Count);
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // A concurrent writer committed after the readback. Split a mixed batch so one
            // raced envelope cannot cause unrelated new envelopes to be reported as duplicates.
            logger.LogDebug(
                "Duplicate key in AuditLogEntity batch ({EnvelopeCount} envelopes); retrying each envelope independently",
                envelopes.Count);

            outcomes.Clear();
            if (envelopes.Count > 1)
            {
                foreach (var envelope in envelopes)
                {
                    if (envelope is null)
                        continue;

                    var envelopeOutcomes = await WriteBatchAsync([envelope], cancellationToken);
                    outcomes.AddRange(envelopeOutcomes);
                }
            }
            else
            {
                foreach (var envelope in envelopes)
                {
                    if (envelope is not null)
                        outcomes.Add(WriteOutcome.Duplicate(envelope.EnvelopeId));
                }
            }
        }
        catch (DbUpdateException ex)
        {
            var isRetryable = IsRetryableDbException(ex);
            var errorMessage = ex.InnerException?.Message ?? ex.Message;

            logger.LogWarning(ex, "Failed to write {EnvelopeCount} entity-change envelope(s)", envelopes.Count);

            outcomes.Clear();
            foreach (var envelope in envelopes)
            {
                if (envelope is not null)
                    outcomes.Add(WriteOutcome.Failed(envelope.EnvelopeId, errorMessage, isRetryable, ex));
            }
        }

        return outcomes;
    }

    private static bool IsRetryableDbException(DbUpdateException ex)
    {
        // Duplicate key is not retryable (it's idempotent success, handled above)
        if (DuplicateKeyDetector.IsDuplicateKey(ex))
            return false;

        var inner = ex.InnerException;
        if (inner is null)
            return false;

        var message = inner.Message;
        return message.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection", StringComparison.OrdinalIgnoreCase);
    }
}
