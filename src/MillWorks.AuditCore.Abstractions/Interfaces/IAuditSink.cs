using MillWorks.AuditCore.Abstractions.Exceptions;
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
    /// <exception cref="AuditWriteException">
    /// Thrown by <c>ImmediateSink</c> when the write fails. Contains details about
    /// which envelopes failed. The interceptor catches this and applies its
    /// <c>AuditFailureMode</c> policy to decide whether to fail-closed or continue.
    /// </exception>
    /// <remarks>
    /// "Accepted" does not mean "committed to the audit store" — for outbox
    /// sinks, it means "committed to the outbox." The audit subsystem is
    /// responsible for durability semantics; callers MUST NOT assume the
    /// envelope is queryable when this method returns.
    /// </remarks>
    Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish multiple audit envelopes to the sink in a single batch. Uses one
    /// database connection and transaction for all envelopes, avoiding connection
    /// pool exhaustion under high-throughput scenarios.
    /// </summary>
    /// <param name="envelopes">The envelopes to publish. Must not be null or contain nulls.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the sink has accepted all envelopes.</returns>
    /// <exception cref="AuditWriteException">
    /// Thrown by <c>ImmediateSink</c> when any envelope in the batch fails to write.
    /// Contains details about failed envelopes. Partial success is not possible for
    /// immediate mode — any failure fails the entire batch.
    /// </exception>
    Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default);
}
