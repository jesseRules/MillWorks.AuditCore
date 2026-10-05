using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
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

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await WriteAttemptAsync(envelopes, cancellationToken);
            }
            catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex) && attempt < maxAttempts)
            {
                // A competing writer may have committed after our lookup. The failed
                // transaction wrote nothing: use a fresh scope to recheck every key and
                // persist the missing rows, never acknowledge the whole batch as duplicate.
                logger.LogDebug(ex,
                    "Concurrent AuditLog insert; rechecking persisted keys (attempt {Attempt})", attempt);
            }
            catch (DbUpdateException ex)
            {
                var isRetryable = DuplicateKeyDetector.IsDuplicateKey(ex) || IsRetryableDbException(ex);
                var errorMessage = ex.InnerException?.Message ?? ex.Message;
                logger.LogWarning(ex, "Failed to write {EnvelopeCount} entity-change envelope(s)", envelopes.Count);
                return envelopes.Where(static e => e is not null)
                    .Select(e => WriteOutcome.Failed(e.EnvelopeId, errorMessage, isRetryable, ex))
                    .ToList();
            }
        }
    }

    private async Task<IReadOnlyList<WriteOutcome>> WriteAttemptAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var auditLogSet = dbContext.Set<AuditLogEntity>();
        var envelopeIds = envelopes.Where(static e => e is not null)
            .Select(static e => e.EnvelopeId).Distinct().ToArray();
        var persistedKeys = new HashSet<(Guid EnvelopeId, string? PropertyName)>();
        // Bound the query size for providers that expand Contains into parameters.
        foreach (var ids in envelopeIds.Chunk(500))
        {
            var rows = await auditLogSet.AsNoTracking()
                .Where(row => row.EnvelopeId.HasValue && ids.Contains(row.EnvelopeId.Value))
                .Select(row => new { row.EnvelopeId, row.PropertyName })
                .ToListAsync(cancellationToken);
            foreach (var row in rows)
                persistedKeys.Add((row.EnvelopeId!.Value, row.PropertyName));
        }

        var stagedKeys = new HashSet<(Guid EnvelopeId, string? PropertyName)>();
        var outcomes = new List<WriteOutcome>(envelopes.Count);

        foreach (var envelope in envelopes)
        {
            if (envelope is null)
                continue;

            var changes = envelope.PropertyChanges;
            var alreadyPersisted = changes is { Count: > 0 }
                ? changes.All(change => persistedKeys.Contains((envelope.EnvelopeId, change.PropertyName)))
                : persistedKeys.Contains((envelope.EnvelopeId, null));
            outcomes.Add(alreadyPersisted
                ? WriteOutcome.Duplicate(envelope.EnvelopeId)
                : WriteOutcome.Success(envelope.EnvelopeId));
            if (changes is { Count: > 0 })
            {
                foreach (var change in changes)
                {
                    var key = (envelope.EnvelopeId, (string?)change.PropertyName);
                    if (persistedKeys.Contains(key) || !stagedKeys.Add(key))
                        continue;

                    var auditLog = new AuditLogEntity
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
                        GovernanceMetadata = SerializeGovernanceIdentity(envelope),
                        CorrelationId = envelope.CorrelationId,
                        IpAddress = envelope.IpAddress,
                        UserAgent = envelope.UserAgent
                    };
                    auditLogSet.Add(auditLog);
                    AddSubjectLinks(dbContext, auditLog, envelope);
                }
            }
            else
            {
                var key = (envelope.EnvelopeId, (string?)null);
                if (persistedKeys.Contains(key) || !stagedKeys.Add(key))
                    continue;

                var auditLog = new AuditLogEntity
                {
                    EnvelopeId = envelope.EnvelopeId,
                    EntityName = envelope.EntityName,
                    EntityId = envelope.EntityId,
                    Action = envelope.Action,
                    Description = envelope.Description,
                    AdditionalData = envelope.AdditionalData,
                    GovernanceMetadata = SerializeGovernanceIdentity(envelope),
                    CorrelationId = envelope.CorrelationId,
                    IpAddress = envelope.IpAddress,
                    UserAgent = envelope.UserAgent
                };
                auditLogSet.Add(auditLog);
                AddSubjectLinks(dbContext, auditLog, envelope);
            }
        }

        if (stagedKeys.Count > 0)
        {
            var written = await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug("Wrote {RowCount} AuditLog row(s) for {EnvelopeCount} envelope(s)",
                written, envelopes.Count);
        }

        // Never acknowledge staged rows until the entire transaction has committed.
        return outcomes;
    }

    private static string? SerializeGovernanceIdentity(AuditEnvelope envelope) =>
        envelope.GovernanceIdentity is null
            ? null
            : JsonSerializer.Serialize(envelope.GovernanceIdentity);

    private static void AddSubjectLinks(
        AuditDbContext dbContext,
        AuditLogEntity auditLog,
        AuditEnvelope envelope)
    {
        if (envelope.SubjectReferences is not { Count: > 0 })
            return;
        if (envelope.GovernanceIdentity is null)
        {
            throw new InvalidOperationException(
                "Audit subject references require a tenant-scoped governance identity.");
        }

        foreach (AuditSubjectReference subject in envelope.SubjectReferences
                     .DistinctBy(static item => (item.SubjectId, item.Relationship)))
        {
            dbContext.AuditLogSubjectLinks.Add(new AuditLogSubjectLinkEntity
            {
                AuditLogId = auditLog.Id,
                TenantId = envelope.GovernanceIdentity.TenantId,
                SubjectId = subject.SubjectId,
                Relationship = subject.Relationship,
                OccurredAt = auditLog.CreatedAt,
                AuditLog = auditLog
            });
        }
    }

    private static bool IsRetryableDbException(DbUpdateException ex)
    {
        var inner = ex.InnerException;
        if (inner is null)
            return false;

        var message = inner.Message;
        return message.Contains("deadlock", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection", StringComparison.OrdinalIgnoreCase);
    }
}
