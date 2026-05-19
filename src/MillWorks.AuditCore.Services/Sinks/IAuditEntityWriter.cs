using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Persists <see cref="AuditEnvelope"/> instances of kind
/// <see cref="MillWorks.AuditCore.Abstractions.Enums.AuditEnvelopeKind.EntityChange"/>
/// as one or more <see cref="AuditLogEntity"/> rows.
/// </summary>
/// <remarks>
/// Internal: the public extension point for sink composition is
/// <see cref="MillWorks.AuditCore.Abstractions.Interfaces.IAuditSink"/> itself.
/// Custom persistence is achieved by replacing the sink, not the writer.
/// </remarks>
internal interface IAuditEntityWriter
{
    /// <summary>
    /// Map the envelope to <see cref="AuditLogEntity"/> row(s) and persist them.
    /// </summary>
    Task WriteEntityChangeAsync(AuditEnvelope envelope, CancellationToken cancellationToken);

    /// <summary>
    /// Map multiple envelopes to <see cref="AuditLogEntity"/> rows and persist them
    /// in a single database round-trip. Uses one connection and transaction for all
    /// envelopes, avoiding connection pool exhaustion under batch writes.
    /// </summary>
    Task WriteBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="IAuditEntityWriter"/> that resolves a scoped
/// <see cref="AuditDbContext"/> and commits audit rows on its own
/// transaction, decoupled from any consumer save in flight.
/// </summary>
internal sealed class AuditDbContextEntityWriter(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditDbContextEntityWriter> logger) : IAuditEntityWriter
{
    public async Task WriteEntityChangeAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        await WriteBatchAsync([envelope], cancellationToken);
    }

    public async Task WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        if (envelopes.Count == 0)
            return;

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
    }
}
