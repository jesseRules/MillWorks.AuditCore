using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Services.Telemetry;
using Moq;
using NUnit.Framework;

namespace MillWorks.AuditCore.Tests.Sinks.Processing;

/// <summary>
/// Tests for metrics emission from <see cref="AuditBatchProcessor"/> using MeterListener
/// to verify actual counter/histogram values.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditBatchProcessorMetricsTests
{
    private Mock<IAuditEntityBatchWriter> _entityWriter = null!;
    private Mock<IAuditEventBatchWriter> _eventWriter = null!;
    private FakeTimeProvider _timeProvider = null!;
    private AuditBatchProcessor _processor = null!;

    [SetUp]
    public void SetUp()
    {
        _entityWriter = new Mock<IAuditEntityBatchWriter>();
        _eventWriter = new Mock<IAuditEventBatchWriter>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero));

        _processor = new AuditBatchProcessor(
            _entityWriter.Object,
            _eventWriter.Object,
            _timeProvider,
            NullLogger<AuditBatchProcessor>.Instance);
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsRowAgeHistogram_ForEachRow()
    {
        var recordedAges = new List<double>();
        var recordedTags = new List<string>();

        using var listener = CreateListener(
            AuditMetrics.Names.OutboxRowAge,
            (value, tags) =>
            {
                recordedAges.Add(value);
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                if (kindTag.Key != null)
                    recordedTags.Add(kindTag.Value?.ToString() ?? "");
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange, createdSecondsAgo: 10),
            CreateRow(AuditEnvelopeKind.EntityChange, createdSecondsAgo: 30),
            CreateRow(AuditEnvelopeKind.ExplicitEvent, createdSecondsAgo: 5),
        };

        SetupWritersToSucceed(rows);

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.That(recordedAges, Has.Count.EqualTo(3));
        Assert.That(recordedAges, Does.Contain(10.0));
        Assert.That(recordedAges, Does.Contain(30.0));
        Assert.That(recordedAges, Does.Contain(5.0));
        Assert.That(recordedTags, Has.Exactly(2).EqualTo("entity_change"));
        Assert.That(recordedTags, Has.Exactly(1).EqualTo("explicit_event"));
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsBatchSizeHistogram_ByEnvelopeKind()
    {
        var recordedBatchSizes = new Dictionary<string, int>();

        using var listener = CreateListener<int>(
            AuditMetrics.Names.OutboxBatchSize,
            (value, tags) =>
            {
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                var kind = kindTag.Value?.ToString() ?? "unknown";
                recordedBatchSizes[kind] = value;
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange),
            CreateRow(AuditEnvelopeKind.EntityChange),
            CreateRow(AuditEnvelopeKind.EntityChange),
            CreateRow(AuditEnvelopeKind.ExplicitEvent),
            CreateRow(AuditEnvelopeKind.ExplicitEvent),
        };

        SetupWritersToSucceed(rows);

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(recordedBatchSizes, Does.ContainKey("entity_change"));
            Assert.That(recordedBatchSizes, Does.ContainKey("explicit_event"));
            Assert.That(recordedBatchSizes["entity_change"], Is.EqualTo(3));
            Assert.That(recordedBatchSizes["explicit_event"], Is.EqualTo(2));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsEnvelopesPublished_OnSuccess()
    {
        var publishedByKind = new Dictionary<string, long>();

        using var listener = CreateCounterListener(
            AuditMetrics.Names.EnvelopesPublished,
            (value, tags) =>
            {
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                var kind = kindTag.Value?.ToString() ?? "unknown";
                publishedByKind.TryGetValue(kind, out var current);
                publishedByKind[kind] = current + value;
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange),
            CreateRow(AuditEnvelopeKind.EntityChange),
            CreateRow(AuditEnvelopeKind.ExplicitEvent),
        };

        SetupWritersToSucceed(rows);

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(publishedByKind, Does.ContainKey("entity_change"));
            Assert.That(publishedByKind, Does.ContainKey("explicit_event"));
            Assert.That(publishedByKind["entity_change"], Is.EqualTo(2));
            Assert.That(publishedByKind["explicit_event"], Is.EqualTo(1));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsEnvelopesDuplicate_OnDuplicateDetection()
    {
        long duplicateCount = 0;
        string? recordedKind = null;

        using var listener = CreateCounterListener(
            AuditMetrics.Names.EnvelopesDuplicate,
            (value, tags) =>
            {
                duplicateCount += value;
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                recordedKind = kindTag.Value?.ToString();
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange),
        };

        _entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WriteOutcome>
            {
                WriteOutcome.Duplicate(rows[0].Envelope.EnvelopeId)
            });

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(duplicateCount, Is.EqualTo(1));
            Assert.That(recordedKind, Is.EqualTo("entity_change"));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsRetryAttempts_OnRetryableFailure()
    {
        long retryCount = 0;
        string? recordedKind = null;
        string? recordedErrorType = null;

        using var listener = CreateCounterListener(
            AuditMetrics.Names.RetryAttempts,
            (value, tags) =>
            {
                retryCount += value;
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                recordedKind = kindTag.Value?.ToString();
                var errorTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.ErrorType);
                recordedErrorType = errorTag.Value?.ToString();
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange),
        };

        var testException = new InvalidOperationException("Transient error");
        _entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(testException);

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(retryCount, Is.EqualTo(1));
            Assert.That(recordedKind, Is.EqualTo("entity_change"));
            Assert.That(recordedErrorType, Is.EqualTo(AuditMetrics.ErrorTypes.Unknown));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_RecordsEnvelopesFailed_OnNonRetryableFailure()
    {
        long failedCount = 0;
        string? recordedKind = null;

        using var listener = CreateCounterListener(
            AuditMetrics.Names.EnvelopesFailed,
            (value, tags) =>
            {
                failedCount += value;
                var kindTag = tags.FirstOrDefault(t => t.Key == AuditMetrics.Tags.EnvelopeKind);
                recordedKind = kindTag.Value?.ToString();
            });

        var rows = new List<ClaimedOutboxRow>
        {
            CreateRow(AuditEnvelopeKind.EntityChange),
        };

        _entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WriteOutcome>
            {
                WriteOutcome.Failed(rows[0].Envelope.EnvelopeId, "Permanent error", isRetryable: false)
            });

        await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(failedCount, Is.EqualTo(1));
            Assert.That(recordedKind, Is.EqualTo("entity_change"));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_NoMetrics_ForEmptyBatch()
    {
        int histogramCalls = 0;
        long counterCalls = 0;

        using var histogramListener = CreateListener(
            AuditMetrics.Names.OutboxBatchSize,
            (_, _) => histogramCalls++);

        using var counterListener = CreateCounterListener(
            AuditMetrics.Names.EnvelopesPublished,
            (value, _) => counterCalls += value);

        await _processor.ProcessBatchAsync([], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(histogramCalls, Is.EqualTo(0));
            Assert.That(counterCalls, Is.EqualTo(0));
        });
    }

    private ClaimedOutboxRow CreateRow(AuditEnvelopeKind kind, int createdSecondsAgo = 0)
    {
        var envelope = new AuditEnvelope
        {
            EnvelopeId = Guid.NewGuid(),
            Kind = kind,
            EntityName = "TestEntity",
            Action = AuditAction.Created,
            EventType = kind == AuditEnvelopeKind.ExplicitEvent ? "TestEvent" : null,
        };

        return new ClaimedOutboxRow
        {
            RowId = Guid.NewGuid(),
            Envelope = envelope,
            AttemptCount = 0,
            CreatedAt = _timeProvider.GetUtcNow().AddSeconds(-createdSecondsAgo),
        };
    }

    private void SetupWritersToSucceed(List<ClaimedOutboxRow> rows)
    {
        var entityRows = rows.Where(r => r.Envelope.Kind == AuditEnvelopeKind.EntityChange).ToList();
        var eventRows = rows.Where(r => r.Envelope.Kind == AuditEnvelopeKind.ExplicitEvent).ToList();

        if (entityRows.Any())
        {
            _entityWriter
                .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(entityRows.Select(r => WriteOutcome.Success(r.Envelope.EnvelopeId)).ToList());
        }

        if (eventRows.Any())
        {
            _eventWriter
                .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(eventRows.Select(r => WriteOutcome.Success(r.Envelope.EnvelopeId)).ToList());
        }
    }

    private MeterListener CreateListener(string metricName, Action<double, KeyValuePair<string, object?>[]> callback)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName && instrument.Name == metricName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == metricName)
                callback(measurement, tags.ToArray());
        });
        listener.Start();
        return listener;
    }

    private MeterListener CreateListener<T>(string metricName, Action<T, KeyValuePair<string, object?>[]> callback) where T : struct
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName && instrument.Name == metricName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<T>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == metricName)
                callback(measurement, tags.ToArray());
        });
        listener.Start();
        return listener;
    }

    private MeterListener CreateCounterListener(string metricName, Action<long, KeyValuePair<string, object?>[]> callback)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName && instrument.Name == metricName)
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == metricName)
                callback(measurement, tags.ToArray());
        });
        listener.Start();
        return listener;
    }

    private sealed class FakeTimeProvider(DateTimeOffset initialTime) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => initialTime;
    }
}
