using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks.Processing;

/// <summary>
/// Processes claimed outbox rows by delegating to appropriate batch writers.
/// Separates processing logic from orchestration (drainer handles claim/release,
/// processor handles sink interaction).
/// </summary>
internal interface IAuditBatchProcessor
{
    /// <summary>
    /// Processes a batch of claimed outbox rows and returns per-row outcomes.
    /// Routes envelopes to the appropriate writer based on envelope kind.
    /// </summary>
    /// <param name="rows">Claimed rows with deserialized envelopes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Per-row outcomes for status transition decisions.</returns>
    Task<BatchProcessingResult> ProcessBatchAsync(
        IReadOnlyList<ClaimedOutboxRow> rows,
        CancellationToken cancellationToken);
}

/// <summary>
/// A claimed outbox row ready for processing.
/// </summary>
internal sealed class ClaimedOutboxRow
{
    /// <summary>
    /// The outbox row ID for status updates after processing.
    /// </summary>
    public required Guid RowId { get; init; }

    /// <summary>
    /// The deserialized envelope to process.
    /// </summary>
    public required AuditEnvelope Envelope { get; init; }

    /// <summary>
    /// Number of previous attempts for this row.
    /// </summary>
    public required int AttemptCount { get; init; }

    /// <summary>
    /// When this outbox row was created. Used for row age metrics.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Result of processing a batch of claimed rows.
/// </summary>
internal sealed class BatchProcessingResult
{
    /// <summary>
    /// Per-row outcomes from processing.
    /// </summary>
    public required IReadOnlyList<RowOutcome> Outcomes { get; init; }

    /// <summary>
    /// Creates an empty result (no rows processed).
    /// </summary>
    public static BatchProcessingResult Empty => new() { Outcomes = [] };
}

/// <summary>
/// Outcome for a single outbox row after processing.
/// </summary>
internal sealed class RowOutcome
{
    /// <summary>
    /// The outbox row ID this outcome corresponds to.
    /// </summary>
    public required Guid RowId { get; init; }

    /// <summary>
    /// The processing status for this row.
    /// </summary>
    public required RowStatus Status { get; init; }

    /// <summary>
    /// Error message when <see cref="Status"/> is <see cref="RowStatus.Failed"/> or
    /// <see cref="RowStatus.RetryLater"/>.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True if the failure is transient and the row may succeed on retry.
    /// Only meaningful when <see cref="Status"/> is <see cref="RowStatus.RetryLater"/>.
    /// </summary>
    public bool IsRetryable { get; init; }

    /// <summary>
    /// Processor-suggested backoff duration before next retry.
    /// Only meaningful when <see cref="Status"/> is <see cref="RowStatus.RetryLater"/>.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>
    /// The exception that caused the failure, if available. Used for error classification in metrics.
    /// </summary>
    public Exception? Exception { get; init; }

    /// <summary>
    /// Creates a successful outcome for a row.
    /// </summary>
    public static RowOutcome Success(Guid rowId) =>
        new() { RowId = rowId, Status = RowStatus.Succeeded };

    /// <summary>
    /// Creates a duplicate-detected outcome (treated as success).
    /// </summary>
    public static RowOutcome Duplicate(Guid rowId) =>
        new() { RowId = rowId, Status = RowStatus.Duplicate };

    /// <summary>
    /// Creates a retry-later outcome for transient failures.
    /// </summary>
    public static RowOutcome Retry(Guid rowId, string errorMessage, TimeSpan? retryAfter = null, Exception? exception = null) =>
        new()
        {
            RowId = rowId,
            Status = RowStatus.RetryLater,
            ErrorMessage = errorMessage,
            IsRetryable = true,
            RetryAfter = retryAfter,
            Exception = exception
        };

    /// <summary>
    /// Creates a failed outcome for non-retryable errors.
    /// </summary>
    public static RowOutcome Failed(Guid rowId, string errorMessage, Exception? exception = null) =>
        new()
        {
            RowId = rowId,
            Status = RowStatus.Failed,
            ErrorMessage = errorMessage,
            IsRetryable = false,
            Exception = exception
        };
}

/// <summary>
/// Processing status for an outbox row.
/// </summary>
internal enum RowStatus
{
    /// <summary>
    /// Row processed successfully.
    /// </summary>
    Succeeded,

    /// <summary>
    /// Row processing failed with a non-retryable error.
    /// </summary>
    Failed,

    /// <summary>
    /// Row was a duplicate of an already-processed envelope (idempotent replay).
    /// </summary>
    Duplicate,

    /// <summary>
    /// Row processing failed with a transient error; should be retried.
    /// </summary>
    RetryLater
}
