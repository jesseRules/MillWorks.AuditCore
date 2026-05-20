using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Services.Telemetry;
using NUnit.Framework;

namespace MillWorks.AuditCore.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="AuditMetrics"/> centralized metrics and error classification.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditMetricsTests
{
    [Test]
    public void MeterName_IsCorrect()
    {
        Assert.That(AuditMetrics.MeterName, Is.EqualTo("MillWorks.AuditCore"));
    }

    [Test]
    public void MetricNames_AreConstantsForConsistency()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditMetrics.Names.OutboxBatchSize, Is.EqualTo("audit.outbox.batch_size"));
            Assert.That(AuditMetrics.Names.OutboxDrainDuration, Is.EqualTo("audit.outbox.drain_duration_ms"));
            Assert.That(AuditMetrics.Names.OutboxRowAge, Is.EqualTo("audit.outbox.row_age_seconds"));
            Assert.That(AuditMetrics.Names.EnvelopesPublished, Is.EqualTo("audit.envelopes.published"));
            Assert.That(AuditMetrics.Names.EnvelopesFailed, Is.EqualTo("audit.envelopes.failed"));
            Assert.That(AuditMetrics.Names.EnvelopesDuplicate, Is.EqualTo("audit.envelopes.duplicate"));
            Assert.That(AuditMetrics.Names.RetryAttempts, Is.EqualTo("audit.outbox.retry_attempts"));
            Assert.That(AuditMetrics.Names.DlqRouted, Is.EqualTo("audit.outbox.dlq_routed"));
            Assert.That(AuditMetrics.Names.LeasesRecovered, Is.EqualTo("audit.outbox.drainer.leases_recovered"));
        });
    }

    [Test]
    public void TagKeys_AreConstantsForConsistency()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditMetrics.Tags.EnvelopeKind, Is.EqualTo("envelope_kind"));
            Assert.That(AuditMetrics.Tags.ErrorType, Is.EqualTo("error_type"));
        });
    }

    [Test]
    public void ErrorTypes_AreConstantsForConsistency()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AuditMetrics.ErrorTypes.Deadlock, Is.EqualTo("deadlock"));
            Assert.That(AuditMetrics.ErrorTypes.Timeout, Is.EqualTo("timeout"));
            Assert.That(AuditMetrics.ErrorTypes.Constraint, Is.EqualTo("constraint"));
            Assert.That(AuditMetrics.ErrorTypes.Serialization, Is.EqualTo("serialization"));
            Assert.That(AuditMetrics.ErrorTypes.Unknown, Is.EqualTo("unknown"));
        });
    }

    #region ClassifyError Tests

    [Test]
    public void ClassifyError_NullException_ReturnsUnknown()
    {
        var result = AuditMetrics.ClassifyError(null);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Unknown));
    }

    [Test]
    public void ClassifyError_JsonException_ReturnsSerialization()
    {
        var ex = new JsonException("Invalid JSON");
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Serialization));
    }

    [Test]
    public void ClassifyError_DbUpdateException_WithoutInner_ReturnsConstraint()
    {
        var ex = new DbUpdateException("Update failed");
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Constraint));
    }

    [Test]
    public void ClassifyError_DbUpdateException_WithConstraintMessage_ReturnsConstraint()
    {
        var inner = new InvalidOperationException("Unique constraint violation");
        var ex = new DbUpdateException("Update failed", inner);
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Constraint));
    }

    [Test]
    public void ClassifyError_DbUpdateException_WithDuplicateMessage_ReturnsConstraint()
    {
        var inner = new InvalidOperationException("Duplicate key value violates unique constraint");
        var ex = new DbUpdateException("Update failed", inner);
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Constraint));
    }

    [Test]
    public void ClassifyError_DbUpdateException_WithTimeoutMessage_ReturnsTimeout()
    {
        var inner = new InvalidOperationException("Connection timeout");
        var ex = new DbUpdateException("Update failed", inner);
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Timeout));
    }

    [Test]
    public void ClassifyError_TimeoutException_ReturnsTimeout()
    {
        var ex = new TimeoutException("Operation timed out");
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Timeout));
    }

    [Test]
    public void ClassifyError_GenericException_ReturnsUnknown()
    {
        var ex = new InvalidOperationException("Something went wrong");
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Unknown));
    }

    [Test]
    public void ClassifyError_OperationCanceledWithTimeoutMessage_ReturnsTimeout()
    {
        var ex = new OperationCanceledException("Operation timeout");
        var result = AuditMetrics.ClassifyError(ex);
        Assert.That(result, Is.EqualTo(AuditMetrics.ErrorTypes.Timeout));
    }

    #endregion

    #region Instrument Creation Tests

    [Test]
    public void OutboxBatchSize_IsHistogram_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.OutboxBatchSize)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Histogram<int>>());
            }
        };
        listener.Start();

        AuditMetrics.OutboxBatchSize.Record(1);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "OutboxBatchSize instrument not found");
            Assert.That(unit, Is.EqualTo("rows"));
        });
    }

    [Test]
    public void OutboxDrainDuration_IsHistogram_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.OutboxDrainDuration)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Histogram<double>>());
            }
        };
        listener.Start();

        AuditMetrics.OutboxDrainDuration.Record(100.0);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "OutboxDrainDuration instrument not found");
            Assert.That(unit, Is.EqualTo("ms"));
        });
    }

    [Test]
    public void OutboxRowAge_IsHistogram_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.OutboxRowAge)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Histogram<double>>());
            }
        };
        listener.Start();

        AuditMetrics.OutboxRowAge.Record(5.0);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "OutboxRowAge instrument not found");
            Assert.That(unit, Is.EqualTo("s"));
        });
    }

    [Test]
    public void EnvelopesPublished_IsCounter_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.EnvelopesPublished)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Counter<long>>());
            }
        };
        listener.Start();

        AuditMetrics.EnvelopesPublished.Add(1);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "EnvelopesPublished instrument not found");
            Assert.That(unit, Is.EqualTo("envelopes"));
        });
    }

    [Test]
    public void LeasesRecovered_IsCounter_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.LeasesRecovered)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Counter<long>>());
            }
        };
        listener.Start();

        AuditMetrics.LeasesRecovered.Add(1);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "LeasesRecovered instrument not found");
            Assert.That(unit, Is.EqualTo("rows"));
        });
    }

    [Test]
    public void DlqRouted_IsCounter_WithCorrectUnit()
    {
        bool found = false;
        string? unit = null;

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName &&
                instrument.Name == AuditMetrics.Names.DlqRouted)
            {
                found = true;
                unit = instrument.Unit;
                Assert.That(instrument, Is.InstanceOf<Counter<long>>());
            }
        };
        listener.Start();

        AuditMetrics.DlqRouted.Add(1);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.True, "DlqRouted instrument not found");
            Assert.That(unit, Is.EqualTo("rows"));
        });
    }

    #endregion
}
