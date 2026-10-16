using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal sealed class OutboxHandoffDispatcher(OutboxStateStore state, ILogger<OutboxHandoffDispatcher> logger,
    IAuditDeadLetterQueue? dlq = null)
{
    internal const string ProjectionEventType = "AuditCore.OutboxFailureProjection.v1";

    internal async Task<int> DispatchAsync(int batchSize, TimeSpan lease, CancellationToken ct)
    {
        // No endpoint is a durable pending disposition, not delivery success.
        if (dlq is null) return 0;
        var claims = await state.ClaimHandoffsAsync(Guid.NewGuid(), batchSize, lease, ct);
        var completed = 0;
        foreach (var claim in claims.Handoffs)
        {
            ct.ThrowIfCancellationRequested();
            if (!await state.OwnsHandoffAsync(claim, ct)) continue;
            var delivered = false;
            try
            {
                var projection = new AuditEvent
                {
                    EventId = claim.Id, EventType = ProjectionEventType,
                    EntityName = "AuditOutboxFailure", Action = AuditAction.Unknown
                };
                projection.CustomFields["HandoffId"] = claim.Id;
                projection.CustomFields["OutboxRowId"] = claim.OutboxRowId;
                projection.CustomFields["EnvelopeJson"] = claim.EnvelopeJson;
                await dlq.StoreFailedEventAsync(projection, reason: claim.ReasonCode);
                delivered = true;
                // Count external writes (possibly redeliveries), not unique acknowledgements.
                AuditMetrics.DlqRouted.Add(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Avoid payload/provider details in logs. Original evidence remains in SQL.
                logger.LogWarning("DLQ projection {HandoffId} was not delivered; retaining for retry", claim.Id);
            }
            var result = await state.AcknowledgeHandoffAsync(Guid.NewGuid(), claim, delivered, ct);
            if (delivered && result.Applied.Length == 1)
            {
                completed++;
                AuditMetrics.HandoffsCompleted.Add(1);
            }
        }
        return completed;
    }
}
