using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Diagnostics;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public sealed class InterceptorDiagnosticsTests
{
    [Test]
    public void Constructor_AcceptsNullDiagnostics()
    {
        var logger = new Mock<ILogger<AuditSaveChangesInterceptor>>();

        Assert.DoesNotThrow(() =>
        {
            _ = new AuditSaveChangesInterceptor(logger.Object, diagnostics: null);
        });
    }

    [Test]
    public void Constructor_AcceptsDiagnosticsInstance()
    {
        var logger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        var diagnostics = new AuditDiagnostics();

        Assert.DoesNotThrow(() =>
        {
            _ = new AuditSaveChangesInterceptor(logger.Object, diagnostics: diagnostics);
        });
    }

    [Test]
    public void AuditDiagnostics_SnapshotCounters_IncrementCorrectly()
    {
        var diag = new AuditDiagnostics();

        diag.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        diag.Increment(AuditDiagnosticCounter.SnapshotSerializationFallback);
        diag.Increment(AuditDiagnosticCounter.SnapshotSerializationTotalFailure);

        Assert.That(diag.SnapshotSerializationFallbackCount, Is.EqualTo(2));
        Assert.That(diag.SnapshotSerializationTotalFailureCount, Is.EqualTo(1));
    }

    [Test]
    public void AuditDiagnostics_DlqCounters_IncrementCorrectly()
    {
        var diag = new AuditDiagnostics();

        diag.Increment(AuditDiagnosticCounter.DlqStoreOperation);
        diag.Increment(AuditDiagnosticCounter.DlqStoreOperation);
        diag.Increment(AuditDiagnosticCounter.DlqStoreFailure);
        diag.Increment(AuditDiagnosticCounter.DlqReplaySuccess);
        diag.Increment(AuditDiagnosticCounter.DlqReplayFailure);
        diag.Increment(AuditDiagnosticCounter.EmergencyFallbackWrite);

        Assert.That(diag.DlqStoreOperationCount, Is.EqualTo(2));
        Assert.That(diag.DlqStoreFailureCount, Is.EqualTo(1));
        Assert.That(diag.DlqReplaySuccessCount, Is.EqualTo(1));
        Assert.That(diag.DlqReplayFailureCount, Is.EqualTo(1));
        Assert.That(diag.EmergencyFallbackWriteCount, Is.EqualTo(1));
    }

    [Test]
    public void AuditDiagnostics_Reset_AfterIncrements_AllZero()
    {
        var diag = new AuditDiagnostics();

        // Increment everything
        foreach (var counter in Enum.GetValues<AuditDiagnosticCounter>())
        {
            diag.Increment(counter);
        }

        diag.Reset();

        Assert.That(diag.SnapshotSerializationFallbackCount, Is.Zero);
        Assert.That(diag.SnapshotSerializationTotalFailureCount, Is.Zero);
        Assert.That(diag.DlqStoreOperationCount, Is.Zero);
        Assert.That(diag.DlqStoreFailureCount, Is.Zero);
        Assert.That(diag.DlqReplaySuccessCount, Is.Zero);
        Assert.That(diag.DlqReplayFailureCount, Is.Zero);
        Assert.That(diag.TamperDetectionRetryCount, Is.Zero);
        Assert.That(diag.EmergencyFallbackWriteCount, Is.Zero);
    }
}
