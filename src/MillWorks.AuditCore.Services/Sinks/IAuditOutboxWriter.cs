namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Writes audit outbox rows inside the consumer transaction through mapped entities or SQL.
/// Used by <see cref="TransactionalOutboxSink"/> to persist audit envelopes
/// inside the consumer's transaction.
/// </summary>
public interface IAuditOutboxWriter
{
    /// <summary>
    /// Inserts an outbox row with the given envelope JSON into the consumer's
    /// transaction. The row is committed when the consumer's SaveChanges commits.
    /// Only content-verified replays are duplicates; changed or unverifiable reuse is rejected.
    /// </summary>
    /// <param name="envelopeJson">Serialized <see cref="MillWorks.AuditCore.Abstractions.Models.AuditEnvelope"/>.</param>
    /// <param name="envelopeVersion">Format version of the serialized envelope.</param>
    /// <param name="idempotencyKey">Unique key for duplicate detection.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if row was inserted; false if duplicate was detected.</returns>
    Task<bool> WriteAsync(string envelopeJson, int envelopeVersion, Guid idempotencyKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts multiple outbox rows within the consumer transaction. Implementations must
    /// implement IAuditIdentityAwareOutboxWriter to be used by the built-in sink. Mapped rows
    /// are provisional until SaveChanges/commit; SQL batches also enlist in the caller transaction.
    /// </summary>
    /// <param name="rows">List of (envelopeJson, envelopeVersion, idempotencyKey) tuples to insert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of rows inserted (excludes duplicates).</returns>
    Task<int> WriteBatchAsync(IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken cancellationToken = default);
}

/// <summary>Explicit opt-in to the content-verified identity protocol. Implementations must
/// compare versioned semantic content for tracked, same-batch, persisted and concurrent
/// identities; commit comparison evidence with source rows; retain it across payload cleanup;
/// and reject conflicting or unverifiable reuse. Key-only custom writers are unsupported.</summary>
public interface IAuditIdentityAwareOutboxWriter : IAuditOutboxWriter { }
