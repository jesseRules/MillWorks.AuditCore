namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Writes audit outbox rows to the consumer's database via raw SQL.
/// Used by <see cref="TransactionalOutboxSink"/> to persist audit envelopes
/// inside the consumer's transaction.
/// </summary>
public interface IAuditOutboxWriter
{
    /// <summary>
    /// Inserts an outbox row with the given envelope JSON into the consumer's
    /// transaction. The row is committed when the consumer's SaveChanges commits.
    /// </summary>
    /// <param name="envelopeJson">Serialized <see cref="MillWorks.AuditCore.Abstractions.Models.AuditEnvelope"/>.</param>
    /// <param name="envelopeVersion">Format version of the serialized envelope.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task WriteAsync(string envelopeJson, int envelopeVersion, CancellationToken cancellationToken = default);
}
