using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks.Writers;

/// <summary>
/// Batch writer for <see cref="Abstractions.Enums.AuditEnvelopeKind.ExplicitEvent"/> envelopes.
/// Persists explicit-event envelopes to the audit store and returns per-envelope outcomes.
/// </summary>
internal interface IAuditEventBatchWriter
{
    /// <summary>
    /// Writes a batch of explicit-event envelopes and returns per-envelope outcomes.
    /// </summary>
    /// <param name="envelopes">Explicit-event envelopes to persist.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One <see cref="WriteOutcome"/> per input envelope, correlated by <see cref="AuditEnvelope.EnvelopeId"/>.</returns>
    Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken);
}
