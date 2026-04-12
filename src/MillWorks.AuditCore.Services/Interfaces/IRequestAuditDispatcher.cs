using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Dispatches completed HTTP request audit events off the request thread.
/// Implementations should keep request-thread latency bounded.
/// </summary>
public interface IRequestAuditDispatcher
{
    /// <summary>
    /// Dispatches a completed request audit event for deferred persistence.
    /// </summary>
    ValueTask DispatchAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
