namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Selects how the request-audit dispatcher should respond when the bounded
/// queue cannot accept a new event (queue full or enqueue-timeout). Paired
/// with <c>AuditMiddlewareOptions.OverflowPolicy</c>. Today only
/// <see cref="Throw"/> matches the implemented dispatcher behavior; the
/// wiring that honors the other values lands in a later checkbox.
/// </summary>
public enum RequestAuditOverflowPolicy
{
    /// <summary>
    /// Overflow is signaled by an exception from the dispatcher; the caller
    /// (request middleware) catches and logs. Matches the current dispatcher
    /// behavior.
    /// </summary>
    Throw = 0,

    /// <summary>
    /// Consumer choice to discard overflow events rather than surface them.
    /// Intended for consumers who prefer event loss over added request
    /// latency.
    /// </summary>
    DropAndLog = 1,

    /// <summary>
    /// Consumer choice to route overflow events to the configured dead-letter
    /// queue for out-of-band processing.
    /// </summary>
    RouteToDeadLetter = 2
}
