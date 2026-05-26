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
    /// Duplicate idempotency keys are handled as success (row already queued).
    /// </summary>
    /// <param name="envelopeJson">Serialized <see cref="MillWorks.AuditCore.Abstractions.Models.AuditEnvelope"/>.</param>
    /// <param name="envelopeVersion">Format version of the serialized envelope.</param>
    /// <param name="idempotencyKey">Unique key for duplicate detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if row was inserted; false if duplicate was detected.</returns>
    Task<bool> WriteAsync(string envelopeJson, int envelopeVersion, Guid idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts multiple outbox rows in a single database round-trip. All rows are
    /// written within the consumer's transaction. Duplicate idempotency keys are
    /// handled as success (rows already queued).
    /// </summary>
    /// <param name="rows">List of (envelopeJson, envelopeVersion, idempotencyKey) tuples to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows inserted (excludes duplicates).</returns>
    Task<int> WriteBatchAsync(IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken cancellationToken = default);
}
