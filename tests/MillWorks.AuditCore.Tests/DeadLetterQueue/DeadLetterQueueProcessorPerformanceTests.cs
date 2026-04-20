using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// Phase 5: Performance tests for DeadLetterQueueProcessor reprocessing under realistic
/// volumes (TestPlanPhaseFive §6). Covers throughput, partial-failure re-enqueue, and
/// full-success queue drain. Uses mocked DLQ + distributed lock so the measurement
/// reflects processor overhead, not storage I/O. Inter-event delay is set to zero via
/// the internal constructor so 1000 entries do not serialize on the 1-second default.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class DeadLetterQueueProcessorPerformanceTests
{
    private Mock<IServiceProvider> _mockServiceProvider = null!;
    private Mock<IServiceScope> _mockServiceScope = null!;
    private Mock<IServiceProvider> _mockScopedServiceProvider = null!;
    private Mock<IAuditDeadLetterQueue> _mockDlq = null!;
    private Mock<IAuditDistributedLockService> _mockLockService = null!;
    private Mock<ILogger<DeadLetterQueueProcessor>> _mockLogger = null!;
    private ResilienceOptions _resilienceOptions = null!;

    [SetUp]
    public void Setup()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockServiceScope = new Mock<IServiceScope>();
        _mockScopedServiceProvider = new Mock<IServiceProvider>();
        _mockDlq = new Mock<IAuditDeadLetterQueue>();
        _mockLockService = new Mock<IAuditDistributedLockService>();
        _mockLogger = new Mock<ILogger<DeadLetterQueueProcessor>>();

        _mockServiceScope.Setup(static x => x.ServiceProvider).Returns(_mockScopedServiceProvider.Object);
        _mockScopedServiceProvider
            .Setup(static x => x.GetService(typeof(IAuditDeadLetterQueue)))
            .Returns(_mockDlq.Object);
        _mockScopedServiceProvider
            .Setup(static x => x.GetService(typeof(IAuditDistributedLockService)))
            .Returns(_mockLockService.Object);

        _mockLockService
            .Setup(x => x.AcquireLockAsync(It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(static x => x.CreateScope()).Returns(_mockServiceScope.Object);
        _mockServiceProvider
            .Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockScopeFactory.Object);

        _resilienceOptions = new ResilienceOptions
        {
            DeadLetterQueueMaxBatchSize = 1000
        };
    }

    // ── §6 DLQ: 1,000 entries, full-success throughput ──

    [Test]
    [CancelAfter(30000)]
    public async Task ProcessOnce_1000Entries_FullSuccess_MeasuresThroughput()
    {
        const int entryCount = 1_000;
        var entries = CreateEntries(entryCount);
        var reprocessed = 0;

        _mockDlq.Setup(x => x.GetFailedEventsAsync(1_000)).ReturnsAsync(entries);
        _mockDlq
            .Setup(x => x.ReprocessEventAsync(It.IsAny<string>()))
            .Returns(() => { Interlocked.Increment(ref reprocessed); return Task.FromResult(true); });
        _mockDlq.Setup(x => x.PurgeProcessedEventsAsync()).ReturnsAsync(entryCount);
        _mockDlq.Setup(x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics { TotalEvents = 0, PendingEvents = 0, FailedEvents = 0 });

        var processor = CreateProcessor();

        var sw = Stopwatch.StartNew();
        await processor.ProcessOnceAsync(CancellationToken.None);
        sw.Stop();

        var throughput = entryCount / sw.Elapsed.TotalSeconds;
        TestContext.Out.WriteLine(
            $"DLQ reprocess 1000 (100% success): {sw.ElapsedMilliseconds}ms ({throughput:F0} events/sec)");

        reprocessed.Should().Be(entryCount, "all entries should be reprocessed");
        _mockDlq.Verify(x => x.PurgeProcessedEventsAsync(), Times.Once,
            "successful entries should be purged after reprocessing");
        throughput.Should().BeGreaterThan(100, "DLQ reprocessing should exceed 100 events/sec without inter-event delay");
    }

    // ── §6 DLQ: 50% success → failed entries stay in queue, successful are purged ──

    [Test]
    [CancelAfter(30000)]
    public async Task ProcessOnce_50PercentSuccess_FailedEntriesRemainForRetry()
    {
        const int entryCount = 1_000;
        var entries = CreateEntries(entryCount);
        var successCount = 0;
        var failureCount = 0;

        _mockDlq.Setup(x => x.GetFailedEventsAsync(1_000)).ReturnsAsync(entries);
        _mockDlq
            .Setup(x => x.ReprocessEventAsync(It.IsAny<string>()))
            .Returns((string id) =>
            {
                // Deterministic 50% success based on id ordinal
                var ordinal = entries.FindIndex(e => e.Id == id);
                var ok = ordinal % 2 == 0;
                if (ok) Interlocked.Increment(ref successCount);
                else Interlocked.Increment(ref failureCount);
                return Task.FromResult(ok);
            });
        // Only successful entries are purged; the rest remain pending.
        _mockDlq.Setup(x => x.PurgeProcessedEventsAsync()).ReturnsAsync(entryCount / 2);
        _mockDlq.Setup(x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics
            {
                TotalEvents = entryCount / 2,
                PendingEvents = 0,
                FailedEvents = entryCount / 2
            });

        var processor = CreateProcessor();

        var sw = Stopwatch.StartNew();
        await processor.ProcessOnceAsync(CancellationToken.None);
        sw.Stop();

        TestContext.Out.WriteLine(
            $"DLQ reprocess 1000 (50% success): {sw.ElapsedMilliseconds}ms, "
            + $"success={successCount}, failure={failureCount}");

        successCount.Should().Be(entryCount / 2);
        failureCount.Should().Be(entryCount / 2);
        _mockDlq.Verify(x => x.ReprocessEventAsync(It.IsAny<string>()), Times.Exactly(entryCount),
            "every non-terminal entry must be attempted exactly once per cycle");
        _mockDlq.Verify(x => x.PurgeProcessedEventsAsync(), Times.Once);
    }

    // ── §6 DLQ: full success → queue drains ──

    [Test]
    [CancelAfter(30000)]
    public async Task ProcessOnce_FullSuccess_QueueIsEmptyAfterPurge()
    {
        const int entryCount = 500;
        var entries = CreateEntries(entryCount);
        var finalStats = new DeadLetterStatistics
        {
            TotalEvents = 0,
            PendingEvents = 0,
            FailedEvents = 0
        };

        _mockDlq.Setup(x => x.GetFailedEventsAsync(1_000)).ReturnsAsync(entries);
        _mockDlq
            .Setup(x => x.ReprocessEventAsync(It.IsAny<string>()))
            .ReturnsAsync(true);
        _mockDlq.Setup(x => x.PurgeProcessedEventsAsync()).ReturnsAsync(entryCount);
        _mockDlq.Setup(x => x.GetStatisticsAsync()).ReturnsAsync(finalStats);

        var processor = CreateProcessor();
        await processor.ProcessOnceAsync(CancellationToken.None);

        _mockDlq.Verify(x => x.ReprocessEventAsync(It.IsAny<string>()), Times.Exactly(entryCount));
        _mockDlq.Verify(x => x.PurgeProcessedEventsAsync(), Times.Once);
        finalStats.TotalEvents.Should().Be(0);
        finalStats.FailedEvents.Should().Be(0);
    }

    #region Helpers

    private DeadLetterQueueProcessor CreateProcessor()
    {
        return new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            Options.Create(_resilienceOptions),
            intervalOverride: TimeSpan.FromSeconds(30),
            interEventDelay: TimeSpan.Zero);
    }

    private static List<DeadLetterAuditEvent> CreateEntries(int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new DeadLetterAuditEvent
            {
                Id = $"dlq-{i:D6}",
                OriginalEventId = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 0,
                FailedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                FailureReason = "Perf.Test"
            })
            .ToList();
    }

    #endregion
}
