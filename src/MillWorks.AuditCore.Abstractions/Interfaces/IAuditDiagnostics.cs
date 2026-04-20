namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Provides aggregate diagnostic counters for audit system health monitoring.
/// Thread-safe — all counters use interlocked operations and can be read from any thread.
/// Register as a singleton and query from health checks, metrics exporters, or admin endpoints.
/// </summary>
public interface IAuditDiagnostics
{
    /// <summary>
    /// Number of interceptor snapshot serialization attempts that fell back to the
    /// minimal payload because the full snapshot could not be serialized.
    /// A sustained non-zero rate indicates entities with unserializable properties.
    /// </summary>
    long SnapshotSerializationFallbackCount { get; }

    /// <summary>
    /// Number of interceptor snapshot serializations where even the minimal fallback
    /// failed. These produce audit records with null AdditionalData.
    /// </summary>
    long SnapshotSerializationTotalFailureCount { get; }

    /// <summary>
    /// Total number of DLQ store operations across all providers.
    /// </summary>
    long DlqStoreOperationCount { get; }

    /// <summary>
    /// Number of DLQ store operations that failed (could not persist the failed event).
    /// </summary>
    long DlqStoreFailureCount { get; }

    /// <summary>
    /// Total number of successful DLQ replay/reprocess operations.
    /// </summary>
    long DlqReplaySuccessCount { get; }

    /// <summary>
    /// Total number of failed DLQ replay/reprocess operations.
    /// </summary>
    long DlqReplayFailureCount { get; }

    /// <summary>
    /// Number of times the tamper detection integrity write was retried due to
    /// duplicate key / concurrency conflicts.
    /// </summary>
    long TamperDetectionRetryCount { get; }

    /// <summary>
    /// Number of emergency fallback file writes (last resort when DLQ is unavailable).
    /// </summary>
    long EmergencyFallbackWriteCount { get; }

    /// <summary>
    /// Number of integrity records successfully flushed by the batched writer.
    /// </summary>
    long IntegrityBatchFlushCount { get; }

    /// <summary>
    /// Number of integrity batch flush failures.
    /// </summary>
    long IntegrityBatchFlushFailureCount { get; }

    /// <summary>
    /// Number of integrity work items successfully reconciled after being left pending.
    /// </summary>
    long IntegrityReconciliationSuccessCount { get; }

    /// <summary>
    /// Number of integrity work items that failed reconciliation (retryable).
    /// </summary>
    long IntegrityReconciliationFailureCount { get; }

    /// <summary>
    /// Number of integrity work items permanently marked as Failed after exceeding max attempts.
    /// </summary>
    long IntegrityPermanentFailureCount { get; }

    /// <summary>
    /// Number of times the EF audit interceptor failed to build audit log records
    /// while processing a save. Incremented whether the failure was swallowed
    /// (Permissive mode) or rethrown (fail-closed modes) — dashboards can pick up
    /// sustained non-zero rates as an audit-pipeline regression signal.
    /// </summary>
    long InterceptorAuditFailureCount { get; }

    /// <summary>
    /// Increments the specified counter by one.
    /// </summary>
    void Increment(AuditDiagnosticCounter counter);

    /// <summary>
    /// Resets all counters to zero. Intended for test isolation only.
    /// </summary>
    void Reset();
}

/// <summary>
/// Named counters for <see cref="IAuditDiagnostics.Increment"/>.
/// </summary>
public enum AuditDiagnosticCounter
{
    SnapshotSerializationFallback,
    SnapshotSerializationTotalFailure,
    DlqStoreOperation,
    DlqStoreFailure,
    DlqReplaySuccess,
    DlqReplayFailure,
    TamperDetectionRetry,
    EmergencyFallbackWrite,
    IntegrityBatchFlush,
    IntegrityBatchFlushFailure,
    IntegrityReconciliationSuccess,
    IntegrityReconciliationFailure,
    IntegrityPermanentFailure,
    InterceptorAuditFailure
}
