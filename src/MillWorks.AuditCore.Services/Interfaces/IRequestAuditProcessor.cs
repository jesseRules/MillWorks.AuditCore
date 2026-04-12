using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Persists deferred HTTP request audit events inside a fresh DI scope.
/// External job systems can invoke this contract directly.
/// </summary>
public interface IRequestAuditProcessor
{
    /// <summary>
    /// Processes a deferred request audit event.
    /// </summary>
    Task ProcessAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
