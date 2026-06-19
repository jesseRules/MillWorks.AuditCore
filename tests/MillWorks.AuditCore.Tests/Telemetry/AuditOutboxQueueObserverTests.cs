using System.Diagnostics.Metrics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Telemetry;
using NUnit.Framework;

namespace MillWorks.AuditCore.Tests.Telemetry;

/// <summary>
/// Tests for <see cref="AuditOutboxQueueObserver"/>: the observable gauges are published on
/// the audit meter with the expected shape, and the values pushed via <c>Update</c> are what
/// the gauge callbacks report (proving the cache-read, no-DB-in-callback contract).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditOutboxQueueObserverTests
{
    private static bool IsQueueGauge(string name) =>
        name == AuditMetrics.Names.OutboxPendingCount ||
        name == AuditMetrics.Names.OutboxInFlightCount ||
        name == AuditMetrics.Names.OutboxOldestPendingAge;

    [Test]
    public void Gauges_ArePublishedAsObservableLongGauges_WithExpectedUnits()
    {
        using var observer = new AuditOutboxQueueObserver();

        var found = new Dictionary<string, (Type Type, string? Unit)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName && IsQueueGauge(instrument.Name))
                found[instrument.Name] = (instrument.GetType(), instrument.Unit);
        };
        listener.Start();

        Assert.Multiple(() =>
        {
            Assert.That(found.ContainsKey(AuditMetrics.Names.OutboxPendingCount), Is.True,
                "pending_count gauge not published");
            Assert.That(found.ContainsKey(AuditMetrics.Names.OutboxInFlightCount), Is.True,
                "inflight_count gauge not published");
            Assert.That(found.ContainsKey(AuditMetrics.Names.OutboxOldestPendingAge), Is.True,
                "oldest_pending_age_seconds gauge not published");

            Assert.That(found[AuditMetrics.Names.OutboxPendingCount].Type, Is.EqualTo(typeof(ObservableGauge<long>)));
            Assert.That(found[AuditMetrics.Names.OutboxInFlightCount].Type, Is.EqualTo(typeof(ObservableGauge<long>)));
            Assert.That(found[AuditMetrics.Names.OutboxOldestPendingAge].Type, Is.EqualTo(typeof(ObservableGauge<long>)));

            Assert.That(found[AuditMetrics.Names.OutboxPendingCount].Unit, Is.EqualTo("rows"));
            Assert.That(found[AuditMetrics.Names.OutboxInFlightCount].Unit, Is.EqualTo("rows"));
            Assert.That(found[AuditMetrics.Names.OutboxOldestPendingAge].Unit, Is.EqualTo("s"));
        });
    }

    [Test]
    public void Gauges_ReportValuesPushedViaUpdate()
    {
        using var observer = new AuditOutboxQueueObserver();

        var measurements = new List<(string Name, long Value)>();
        using var listener = BuildListener(measurements);
        listener.Start();

        observer.Update(pendingCount: 7, inFlightCount: 3, oldestPendingAgeSeconds: 42);
        listener.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxPendingCount, 7L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxInFlightCount, 3L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxOldestPendingAge, 42L)));
        });
    }

    [Test]
    public void Gauges_ReportZeroBeforeAnySample()
    {
        using var observer = new AuditOutboxQueueObserver();

        var measurements = new List<(string Name, long Value)>();
        using var listener = BuildListener(measurements);
        listener.Start();

        listener.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxPendingCount, 0L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxInFlightCount, 0L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxOldestPendingAge, 0L)));
        });
    }

    [Test]
    public void Update_OverwritesPreviousValues()
    {
        using var observer = new AuditOutboxQueueObserver();

        var measurements = new List<(string Name, long Value)>();
        using var listener = BuildListener(measurements);
        listener.Start();

        observer.Update(1, 1, 1);
        observer.Update(9, 8, 7);
        listener.RecordObservableInstruments();

        Assert.Multiple(() =>
        {
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxPendingCount, 9L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxInFlightCount, 8L)));
            Assert.That(measurements, Does.Contain((AuditMetrics.Names.OutboxOldestPendingAge, 7L)));
            Assert.That(measurements, Does.Not.Contain((AuditMetrics.Names.OutboxPendingCount, 1L)));
        });
    }

    private static MeterListener BuildListener(List<(string Name, long Value)> sink)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName && IsQueueGauge(instrument.Name))
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            sink.Add((instrument.Name, value)));
        return listener;
    }
}

/// <summary>
/// Tests for <see cref="AuditOutboxDrainer.ComputeQueueDepthAsync"/> — the index-backed
/// aggregate query that feeds the queue-depth gauges. Uses SQLite for real query semantics.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class OutboxQueueDepthSamplingTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private AuditDbContext _auditCtx = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(_connection)
            .Options;

        _auditCtx = new AuditDbContext(options);
        _auditCtx.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown() => Dispose();

    public void Dispose()
    {
        _auditCtx?.Dispose();
        _connection?.Dispose();
    }

    private static AuditOutboxEntity Row(AuditOutboxStatus status, DateTimeOffset createdAt) => new()
    {
        EnvelopeJson = "{}",
        Status = status,
        CreatedAt = createdAt,
        IdempotencyKey = Guid.NewGuid(),
    };

    [Test]
    public async Task ComputeQueueDepthAsync_CountsByStatus_AndReportsOldestPendingAge()
    {
        var now = DateTimeOffset.UtcNow;

        _auditCtx.AuditOutbox.AddRange(
            Row(AuditOutboxStatus.Pending, now.AddSeconds(-120)),  // oldest pending
            Row(AuditOutboxStatus.Pending, now.AddSeconds(-30)),
            Row(AuditOutboxStatus.InFlight, now.AddSeconds(-10)),
            Row(AuditOutboxStatus.Completed, now.AddSeconds(-300)),
            Row(AuditOutboxStatus.Failed, now.AddSeconds(-300)));
        await _auditCtx.SaveChangesAsync();

        var (pending, inFlight, oldestAge) =
            await AuditOutboxDrainer.ComputeQueueDepthAsync(_auditCtx, now, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EqualTo(2), "pending count");
            Assert.That(inFlight, Is.EqualTo(1), "in-flight count");
            // Oldest pending is the -120s row; allow a small tolerance for SQLite round-trip.
            Assert.That(oldestAge, Is.EqualTo(120L).Within(2));
        });
    }

    [Test]
    public async Task ComputeQueueDepthAsync_EmptyQueue_ReturnsZeros()
    {
        var now = DateTimeOffset.UtcNow;

        var (pending, inFlight, oldestAge) =
            await AuditOutboxDrainer.ComputeQueueDepthAsync(_auditCtx, now, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EqualTo(0));
            Assert.That(inFlight, Is.EqualTo(0));
            Assert.That(oldestAge, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task ComputeQueueDepthAsync_NoPendingRows_OldestAgeIsZero_ButInFlightCounted()
    {
        var now = DateTimeOffset.UtcNow;

        _auditCtx.AuditOutbox.AddRange(
            Row(AuditOutboxStatus.InFlight, now.AddSeconds(-90)),
            Row(AuditOutboxStatus.Completed, now.AddSeconds(-90)));
        await _auditCtx.SaveChangesAsync();

        var (pending, inFlight, oldestAge) =
            await AuditOutboxDrainer.ComputeQueueDepthAsync(_auditCtx, now, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(pending, Is.EqualTo(0));
            Assert.That(inFlight, Is.EqualTo(1));
            Assert.That(oldestAge, Is.EqualTo(0), "no pending rows → oldest age 0, not the in-flight row's age");
        });
    }
}
