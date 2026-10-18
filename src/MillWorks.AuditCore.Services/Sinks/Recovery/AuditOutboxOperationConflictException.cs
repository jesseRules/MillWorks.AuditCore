namespace MillWorks.AuditCore.Services.Sinks.Recovery;

/// <summary>A durable operation identity was reused for different input. Do not retry it with changed arguments.</summary>
public sealed class AuditOutboxOperationConflictException(Guid operationId)
    : InvalidOperationException("An outbox operation identity was reused with different input.")
{
    /// <summary>The previously recorded operation identity.</summary>
    public Guid OperationId { get; } = operationId;
}
