using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Diagnostics;

/// <summary>
/// Thread-safe aggregate diagnostic counters for audit system health monitoring.
/// Registered as a singleton — counters accumulate across the lifetime of the application.
/// </summary>
public sealed class AuditDiagnostics : IAuditDiagnostics
{
    private long _snapshotSerializationFallbackCount;
    private long _snapshotSerializationTotalFailureCount;
    private long _dlqStoreOperationCount;
    private long _dlqStoreFailureCount;
    private long _dlqReplaySuccessCount;
    private long _dlqReplayFailureCount;
    private long _tamperDetectionRetryCount;
    private long _emergencyFallbackWriteCount;
    private long _integrityBatchFlushCount;
    private long _integrityBatchFlushFailureCount;
    private long _integrityReconciliationSuccessCount;
    private long _integrityReconciliationFailureCount;
    private long _integrityPermanentFailureCount;
    private long _interceptorAuditFailureCount;

    /// <inheritdoc />
    public long SnapshotSerializationFallbackCount =>
        Interlocked.Read(ref _snapshotSerializationFallbackCount);

    /// <inheritdoc />
    public long SnapshotSerializationTotalFailureCount =>
        Interlocked.Read(ref _snapshotSerializationTotalFailureCount);

    /// <inheritdoc />
    public long DlqStoreOperationCount =>
        Interlocked.Read(ref _dlqStoreOperationCount);

    /// <inheritdoc />
    public long DlqStoreFailureCount =>
        Interlocked.Read(ref _dlqStoreFailureCount);

    /// <inheritdoc />
    public long DlqReplaySuccessCount =>
        Interlocked.Read(ref _dlqReplaySuccessCount);

    /// <inheritdoc />
    public long DlqReplayFailureCount =>
        Interlocked.Read(ref _dlqReplayFailureCount);

    /// <inheritdoc />
    public long TamperDetectionRetryCount =>
        Interlocked.Read(ref _tamperDetectionRetryCount);

    /// <inheritdoc />
    public long EmergencyFallbackWriteCount =>
        Interlocked.Read(ref _emergencyFallbackWriteCount);

    /// <inheritdoc />
    public long IntegrityBatchFlushCount =>
        Interlocked.Read(ref _integrityBatchFlushCount);

    /// <inheritdoc />
    public long IntegrityBatchFlushFailureCount =>
        Interlocked.Read(ref _integrityBatchFlushFailureCount);

    /// <inheritdoc />
    public long IntegrityReconciliationSuccessCount =>
        Interlocked.Read(ref _integrityReconciliationSuccessCount);

    /// <inheritdoc />
    public long IntegrityReconciliationFailureCount =>
        Interlocked.Read(ref _integrityReconciliationFailureCount);

    /// <inheritdoc />
    public long IntegrityPermanentFailureCount =>
        Interlocked.Read(ref _integrityPermanentFailureCount);

    /// <inheritdoc />
    public long InterceptorAuditFailureCount =>
        Interlocked.Read(ref _interceptorAuditFailureCount);

    /// <inheritdoc />
    public void Increment(AuditDiagnosticCounter counter)
    {
        switch (counter)
        {
            case AuditDiagnosticCounter.SnapshotSerializationFallback:
                Interlocked.Increment(ref _snapshotSerializationFallbackCount);
                break;
            case AuditDiagnosticCounter.SnapshotSerializationTotalFailure:
                Interlocked.Increment(ref _snapshotSerializationTotalFailureCount);
                break;
            case AuditDiagnosticCounter.DlqStoreOperation:
                Interlocked.Increment(ref _dlqStoreOperationCount);
                break;
            case AuditDiagnosticCounter.DlqStoreFailure:
                Interlocked.Increment(ref _dlqStoreFailureCount);
                break;
            case AuditDiagnosticCounter.DlqReplaySuccess:
                Interlocked.Increment(ref _dlqReplaySuccessCount);
                break;
            case AuditDiagnosticCounter.DlqReplayFailure:
                Interlocked.Increment(ref _dlqReplayFailureCount);
                break;
            case AuditDiagnosticCounter.TamperDetectionRetry:
                Interlocked.Increment(ref _tamperDetectionRetryCount);
                break;
            case AuditDiagnosticCounter.EmergencyFallbackWrite:
                Interlocked.Increment(ref _emergencyFallbackWriteCount);
                break;
            case AuditDiagnosticCounter.IntegrityBatchFlush:
                Interlocked.Increment(ref _integrityBatchFlushCount);
                break;
            case AuditDiagnosticCounter.IntegrityBatchFlushFailure:
                Interlocked.Increment(ref _integrityBatchFlushFailureCount);
                break;
            case AuditDiagnosticCounter.IntegrityReconciliationSuccess:
                Interlocked.Increment(ref _integrityReconciliationSuccessCount);
                break;
            case AuditDiagnosticCounter.IntegrityReconciliationFailure:
                Interlocked.Increment(ref _integrityReconciliationFailureCount);
                break;
            case AuditDiagnosticCounter.IntegrityPermanentFailure:
                Interlocked.Increment(ref _integrityPermanentFailureCount);
                break;
            case AuditDiagnosticCounter.InterceptorAuditFailure:
                Interlocked.Increment(ref _interceptorAuditFailureCount);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(counter), counter,
                    $"Unhandled diagnostic counter: {counter}");
        }
    }

    /// <inheritdoc />
    public void Reset()
    {
        Interlocked.Exchange(ref _snapshotSerializationFallbackCount, 0);
        Interlocked.Exchange(ref _snapshotSerializationTotalFailureCount, 0);
        Interlocked.Exchange(ref _dlqStoreOperationCount, 0);
        Interlocked.Exchange(ref _dlqStoreFailureCount, 0);
        Interlocked.Exchange(ref _dlqReplaySuccessCount, 0);
        Interlocked.Exchange(ref _dlqReplayFailureCount, 0);
        Interlocked.Exchange(ref _tamperDetectionRetryCount, 0);
        Interlocked.Exchange(ref _emergencyFallbackWriteCount, 0);
        Interlocked.Exchange(ref _integrityBatchFlushCount, 0);
        Interlocked.Exchange(ref _integrityBatchFlushFailureCount, 0);
        Interlocked.Exchange(ref _integrityReconciliationSuccessCount, 0);
        Interlocked.Exchange(ref _integrityReconciliationFailureCount, 0);
        Interlocked.Exchange(ref _integrityPermanentFailureCount, 0);
        Interlocked.Exchange(ref _interceptorAuditFailureCount, 0);
    }
}
