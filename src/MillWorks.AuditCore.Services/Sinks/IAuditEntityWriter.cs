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
}

/// <summary>
/// Default <see cref="IAuditEntityWriter"/> that resolves a fresh scoped
/// <see cref="AuditApplicationDbContext"/> per call and commits audit rows on
/// its own transaction, decoupled from any consumer save in flight.
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

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();

        var changes = envelope.PropertyChanges;
        if (changes is { Count: > 0 })
        {
            foreach (var change in changes)
            {
                dbContext.Set<AuditLogEntity>().Add(new AuditLogEntity
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
            }
        }
        else
        {
            dbContext.Set<AuditLogEntity>().Add(new AuditLogEntity
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
        }

        var written = await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug(
            "Wrote {RowCount} AuditLog row(s) for entity {EntityName} action {Action}",
            written, envelope.EntityName, envelope.Action);
    }
}
