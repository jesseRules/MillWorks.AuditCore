using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks.Writers;

/// <summary>
/// Batch writer for <see cref="Abstractions.Enums.AuditEnvelopeKind.EntityChange"/> envelopes.
/// Persists entity-change envelopes to the audit store and returns per-envelope outcomes.
/// </summary>
internal interface IAuditEntityBatchWriter
{
    /// <summary>
    /// Writes a batch of entity-change envelopes and returns per-envelope outcomes.
    /// All envelopes are persisted in a single database transaction.
    /// </summary>
    /// <param name="envelopes">Entity-change envelopes to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One <see cref="WriteOutcome"/> per input envelope, correlated by <see cref="AuditEnvelope.EnvelopeId"/>.</returns>
    Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken);
}
