using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Default <see cref="IAuditSink"/>: persists each envelope synchronously on
/// publish. <see cref="AuditEnvelopeKind.EntityChange"/> envelopes are written
/// via <see cref="IAuditEntityWriter"/>;
/// <see cref="AuditEnvelopeKind.ExplicitEvent"/> envelopes are forwarded to
/// <see cref="IAuditLogger.LogAsync(AuditEvent, CancellationToken)"/>.
/// </summary>
internal sealed class ImmediateSink(
    IAuditLogger auditLogger,
    IAuditEntityWriter auditEntityWriter,
    ILogger<ImmediateSink> logger) : IAuditSink
{
    /// <inheritdoc />
    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.Kind)
        {
            case AuditEnvelopeKind.EntityChange:
                await auditEntityWriter.WriteEntityChangeAsync(envelope, cancellationToken);
                break;

            case AuditEnvelopeKind.ExplicitEvent:
                await auditLogger.LogAsync(BuildAuditEvent(envelope), cancellationToken);
                break;

            default:
                logger.LogError("Unknown AuditEnvelopeKind {Kind}", envelope.Kind);
                throw new InvalidOperationException(
                    $"Unhandled AuditEnvelopeKind: {envelope.Kind}");
        }
    }

    private static AuditEvent BuildAuditEvent(AuditEnvelope envelope)
    {
        var auditEvent = new AuditEvent
        {
            EventType = envelope.EventType ?? string.Empty,
            EntityName = envelope.EntityName,
            Action = envelope.Action,
            StartDate = envelope.OccurredAt,
            CorrelationId = envelope.CorrelationId,
            IpAddress = envelope.IpAddress,
            UserAgent = envelope.UserAgent,
            AspNetUserId = envelope.UserId
        };

        if (envelope.EntityId is { } entityId)
        {
            auditEvent.KeyValues["Id"] = entityId;
        }

        if (envelope.Description is { } description)
        {
            auditEvent.CustomFields["Description"] = description;
        }

        if (envelope.AdditionalData is { } additionalData)
        {
            auditEvent.CustomFields["AdditionalData"] = additionalData;
        }

        return auditEvent;
    }
}
