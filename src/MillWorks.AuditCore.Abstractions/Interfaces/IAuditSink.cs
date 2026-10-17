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
    /// Thrown by <c>ImmediateSink</c> when its writer returns a failed outcome. Contains details about
    /// which envelopes failed. The interceptor catches this and applies its
    /// <c>AuditFailureMode</c> policy to decide whether to fail-closed or continue.
    /// </exception>
    /// <remarks>
    /// "Accepted" does not mean "committed to the audit store" — for outbox
    /// sinks, it means accepted inside the consumer transaction, which can still roll back. The audit subsystem is
    /// responsible for durability semantics; callers MUST NOT assume the
    /// envelope is queryable when this method returns.
    /// </remarks>
    Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default);

    /// <summary>
    /// Publish multiple audit envelopes using the selected sink's acceptance semantics.
    /// This method does not promise one connection or transaction for the whole batch.
    /// </summary>
    /// <param name="envelopes">The envelopes to publish. Must not be null or contain nulls. An empty batch is a no-op.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the sink has accepted all envelopes.</returns>
    /// <exception cref="ArgumentNullException">The batch is null.</exception>
    /// <exception cref="ArgumentException">A batch entry is null. Rejected before either immediate writer or the outbox writer runs.</exception>
    /// <exception cref="AuditWriteException">
    /// In immediate mode, one or more returned writer outcomes indicate failure.
    /// <see cref="AuditWriteException.FailedEnvelopeIds"/> correlates those outcomes with the
    /// original inputs. Other envelopes may already be persisted; this exception does not
    /// roll back successful writes or prove that a reported failed identity is absent.
    /// </exception>
    /// <remarks>
    /// Immediate entity-change and explicit-event writers commit independently. Entity-change
    /// attempts are atomic and duplicate-key retries recheck the batch in a fresh scope.
    /// Explicit-event duplicate-race fallback can commit events individually before a later
    /// write fails. Writer exceptions and cancellation propagate and can interrupt processing
    /// without a complete set of per-envelope results; earlier writes may remain committed.
    /// Preserve the original envelopes and their identities when retrying the same work.
    ///
    /// Transactional outbox acceptance occurs inside the consumer transaction. A successful
    /// return does not commit that transaction, prevent its later rollback, or mean that the
    /// final audit records are queryable. Committed outbox work is processed asynchronously.
    /// </remarks>
    Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default);
}
