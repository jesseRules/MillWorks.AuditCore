using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Diagnostics;

namespace MillWorks.AuditCore.Tests.Diagnostics;

[TestFixture]
[Category("Unit")]
public sealed class AuditDiagnosticsTests
{
    private AuditDiagnostics _diagnostics = null!;

    [SetUp]
    public void SetUp()
    {
        _diagnostics = new AuditDiagnostics();
    }

    [Test]
    public void AllCounters_InitiallyZero()
    {
        Assert.That(_diagnostics.SnapshotSerializationFallbackCount, Is.Zero);
        Assert.That(_diagnostics.SnapshotSerializationTotalFailureCount, Is.Zero);
        Assert.That(_diagnostics.DlqStoreOperationCount, Is.Zero);
        Assert.That(_diagnostics.DlqStoreFailureCount, Is.Zero);
        Assert.That(_diagnostics.DlqReplaySuccessCount, Is.Zero);
        Assert.That(_diagnostics.DlqReplayFailureCount, Is.Zero);
        Assert.That(_diagnostics.TamperDetectionRetryCount, Is.Zero);
        Assert.That(_diagnostics.EmergencyFallbackWriteCount, Is.Zero);
    }

    [Test]
    public void Increment_SnapshotSerializationFallback_IncrementsByOne()
    {
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);

        Assert.That(_diagnostics.SnapshotSerializationFallbackCount, Is.EqualTo(3));
    }

    [Test]
    public void Increment_AllCounterTypes_IndependentlyTracked()
    {
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationTotalFailure);
        _diagnostics.Increment(AuditDiagnosticCounter.DlqStoreOperation);
        _diagnostics.Increment(AuditDiagnosticCounter.DlqStoreFailure);
        _diagnostics.Increment(AuditDiagnosticCounter.DlqReplaySuccess);
        _diagnostics.Increment(AuditDiagnosticCounter.DlqReplayFailure);
        _diagnostics.Increment(AuditDiagnosticCounter.TamperDetectionRetry);
        _diagnostics.Increment(AuditDiagnosticCounter.EmergencyFallbackWrite);

        Assert.That(_diagnostics.SnapshotSerializationFallbackCount, Is.EqualTo(1));
        Assert.That(_diagnostics.SnapshotSerializationTotalFailureCount, Is.EqualTo(1));
        Assert.That(_diagnostics.DlqStoreOperationCount, Is.EqualTo(1));
        Assert.That(_diagnostics.DlqStoreFailureCount, Is.EqualTo(1));
        Assert.That(_diagnostics.DlqReplaySuccessCount, Is.EqualTo(1));
        Assert.That(_diagnostics.DlqReplayFailureCount, Is.EqualTo(1));
        Assert.That(_diagnostics.TamperDetectionRetryCount, Is.EqualTo(1));
        Assert.That(_diagnostics.EmergencyFallbackWriteCount, Is.EqualTo(1));
    }

    [Test]
    public void Reset_ClearsAllCounters()
    {
        _diagnostics.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        _diagnostics.Increment(AuditDiagnosticCounter.DlqStoreOperation);
        _diagnostics.Increment(AuditDiagnosticCounter.EmergencyFallbackWrite);

        _diagnostics.Reset();

        Assert.That(_diagnostics.SnapshotSerializationFallbackCount, Is.Zero);
        Assert.That(_diagnostics.DlqStoreOperationCount, Is.Zero);
        Assert.That(_diagnostics.EmergencyFallbackWriteCount, Is.Zero);
    }

    [Test]
    public void Increment_InvalidCounter_ThrowsArgumentOutOfRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            _diagnostics.Increment((AuditDiagnosticCounter)999));
    }

    [Test]
    public void Increment_ThreadSafety_ConcurrentIncrements()
    {
        const int threadCount = 10;
        const int incrementsPerThread = 1000;

        var tasks = Enumerable.Range(0, threadCount).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < incrementsPerThread; i++)
            {
                _diagnostics.Increment(AuditDiagnosticCounter.DlqStoreOperation);
            }
        }));

        Task.WhenAll(tasks).GetAwaiter().GetResult();

        Assert.That(_diagnostics.DlqStoreOperationCount,
            Is.EqualTo(threadCount * incrementsPerThread));
    }
}
