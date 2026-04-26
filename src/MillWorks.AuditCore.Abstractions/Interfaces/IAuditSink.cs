using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Receives <see cref="AuditEnvelope"/> objects from producers (the EF
/// <c>AuditSaveChangesInterceptor</c> and explicit <c>IAuditLogger</c> callers)
/// and owns persistence semantics. Implementations decide whether envelopes
/// commit synchronously, route through a transactional outbox, or batch.
/// </summary>
public interface IAuditSink
{
    /// <summary>
    /// Publish an audit envelope to the sink. The sink decides where and
    /// when to commit it (immediate, transactional outbox, batched, etc.).
    /// </summary>
    /// <param name="envelope">The envelope to publish. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the sink has accepted the envelope.</returns>
    /// <remarks>
    /// "Accepted" does not mean "committed to the audit store" — for outbox
    /// sinks, it means "committed to the outbox." The audit subsystem is
    /// responsible for durability semantics; callers MUST NOT assume the
    /// envelope is queryable when this method returns.
    /// </remarks>
    Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default);
}
