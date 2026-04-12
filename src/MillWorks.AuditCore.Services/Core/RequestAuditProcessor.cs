using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Default processor for deferred HTTP request audit events.
/// </summary>
public sealed class RequestAuditProcessor(
    IAuditLogger auditLogger,
    ILogger<RequestAuditProcessor> logger,
    IAuditDeadLetterQueue? deadLetterQueue = null)
    : IRequestAuditProcessor
{
    /// <inheritdoc />
    public async Task ProcessAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        try
        {
            await auditLogger.LogAsync(auditEvent, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (deadLetterQueue is null)
                throw;

            logger.LogError(ex,
                "Deferred request audit persistence failed for event {EventId}. Storing in DLQ.",
                auditEvent.EventId);

            await deadLetterQueue.StoreFailedEventAsync(
                auditEvent,
                ex,
                "Deferred request audit persistence failed");
        }
    }
}
