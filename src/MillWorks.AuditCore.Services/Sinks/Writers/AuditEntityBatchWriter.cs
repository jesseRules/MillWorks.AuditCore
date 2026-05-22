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
            var totalRows = 0;

            foreach (var envelope in envelopes)
            {
                if (envelope is null)
                    continue;

                var changes = envelope.PropertyChanges;
                if (changes is { Count: > 0 })
                {
                    foreach (var change in changes)
                    {
                        auditLogSet.Add(new AuditLogEntity
                        {
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
                    }
                }
                else
                {
                    auditLogSet.Add(new AuditLogEntity
                    {
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
                }
            }

            var written = await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug(
                "Wrote {RowCount} AuditLog row(s) for {EnvelopeCount} envelope(s)",
                written, envelopes.Count);

            foreach (var envelope in envelopes)
            {
                if (envelope is not null)
                    outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
            }
        }
        catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
        {
            // Duplicate key violation for entity-change envelopes is unexpected.
            // AuditLogEntity uses auto-generated GUIDs for PKs and has no unique
            // constraints on business keys. A duplicate key error here indicates
            // either a data bug or an unrelated constraint violation - not an
            // idempotent replay. Mark as failed rather than silently succeeding.
            var errorMessage = ex.InnerException?.Message ?? ex.Message;
            logger.LogError(ex,
                "Unexpected duplicate key in AuditLogEntity batch ({EnvelopeCount} envelopes): {Error}",
                envelopes.Count, errorMessage);

            foreach (var envelope in envelopes)
            {
                if (envelope is not null)
                    outcomes.Add(WriteOutcome.Failed(envelope.EnvelopeId, $"Unexpected duplicate key: {errorMessage}", isRetryable: false, ex));
            }
        }
        catch (DbUpdateException ex)
        {
            var isRetryable = IsRetryableDbException(ex);
            var errorMessage = ex.InnerException?.Message ?? ex.Message;

            logger.LogWarning(ex, "Failed to write {EnvelopeCount} entity-change envelope(s)", envelopes.Count);

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
