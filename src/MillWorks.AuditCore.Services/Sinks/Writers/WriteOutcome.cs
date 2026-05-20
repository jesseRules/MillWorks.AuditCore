namespace MillWorks.AuditCore.Services.Sinks.Writers;

/// <summary>
/// Per-envelope write result returned by batch writers.
/// Correlates outcomes to envelopes via <see cref="EnvelopeId"/> for deterministic
/// result tracking without relying on batch ordering.
/// </summary>
internal sealed class WriteOutcome
{
    /// <summary>
    /// The <see cref="Abstractions.Models.AuditEnvelope.EnvelopeId"/> this outcome corresponds to.
    /// </summary>
    public required Guid EnvelopeId { get; init; }

    /// <summary>
    /// True if the envelope was successfully persisted (includes duplicate detection as success).
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>
    /// Human-readable error message when <see cref="Succeeded"/> is false.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True if this envelope was detected as a duplicate of an already-persisted record.
    /// Duplicates are considered successful (idempotent replay).
    /// </summary>
    public bool IsDuplicate { get; init; }

    /// <summary>
    /// True if the failure is transient and the envelope may succeed on retry.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>
    /// The exception that caused the failure, if available. Used for error classification in metrics.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates a successful outcome for the given envelope.
    /// </summary>
    public static WriteOutcome Success(Guid envelopeId) =>
        new() { EnvelopeId = envelopeId, Succeeded = true };

    /// <summary>
    /// Creates a duplicate-detected outcome (treated as success).
    /// </summary>
    public static WriteOutcome Duplicate(Guid envelopeId) =>
        new() { EnvelopeId = envelopeId, Succeeded = true, IsDuplicate = true };

    /// <summary>
    /// Creates a failed outcome with the given error.
    /// </summary>
    public static WriteOutcome Failed(Guid envelopeId, string errorMessage, bool isRetryable = false, Exception? exception = null) =>
        new() { EnvelopeId = envelopeId, Succeeded = false, ErrorMessage = errorMessage, IsRetryable = isRetryable, Exception = exception };
}
