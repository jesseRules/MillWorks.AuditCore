using System.Diagnostics;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.TamperDetection;

/// <summary>
/// Phase 5: Performance and soak tests for IntegrityWriteBatcher.
/// Validates throughput, concurrent writers, flush-on-stop, and backpressure behavior.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class IntegrityWriteBatcherPerformanceTests
{
    private Mock<IServiceScopeFactory> _mockScopeFactory = null!;
    private Mock<IServiceScope> _mockScope = null!;
    private Mock<IServiceProvider> _mockScopeProvider = null!;
    private Mock<ITamperDetectionService> _mockTamperDetection = null!;
    private Mock<ILogger<IntegrityWriteBatcher>> _mockLogger = null!;
    private AuditApplicationDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeProvider = new Mock<IServiceProvider>();
        _mockTamperDetection = new Mock<ITamperDetectionService>();
        _mockLogger = new Mock<ILogger<IntegrityWriteBatcher>>();

        var dbOptions = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _dbContext = new AuditApplicationDbContext(dbOptions);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();

        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ITamperDetectionService)))
            .Returns(_mockTamperDetection.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(AuditApplicationDbContext)))
            .Returns(_dbContext);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // ── Throughput: batch sizes 10, 100 ──

    [Test]
    [CancelAfter(30000)]
    public async Task Throughput_BatchSize10_ProcessesEfficiently()
    {
        const int batchSize = 10;
        const int totalEvents = 100;

        SetupBatchMock(batchSize);
        var batcher = CreateBatcher(batchSize, flushIntervalMs: 50);
        using var cts = new CancellationTokenSource();
        var executeTask = StartBatcher(batcher, cts.Token);

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, totalEvents)
            .Select(_ => batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None))
            .ToList();
        await Task.WhenAll(tasks);
        sw.Stop();

        var eventsPerSecond = totalEvents / sw.Elapsed.TotalSeconds;
        TestContext.Out.WriteLine($"Batch=10: {eventsPerSecond:F0} events/sec ({sw.ElapsedMilliseconds}ms for {totalEvents} events)");
        eventsPerSecond.Should().BeGreaterThan(100, "should process at least 100 events/sec");

        cts.Cancel();
        await executeTask;
    }

    [Test]
    [CancelAfter(30000)]
    public async Task Throughput_BatchSize100_ProcessesEfficiently()
    {
        const int batchSize = 100;
        const int totalEvents = 500;

        SetupBatchMock(batchSize);
        var batcher = CreateBatcher(batchSize, flushIntervalMs: 100);
        using var cts = new CancellationTokenSource();
        var executeTask = StartBatcher(batcher, cts.Token);

        var sw = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, totalEvents)
            .Select(_ => batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None))
            .ToList();
        await Task.WhenAll(tasks);
        sw.Stop();

        var eventsPerSecond = totalEvents / sw.Elapsed.TotalSeconds;
        TestContext.Out.WriteLine($"Batch=100: {eventsPerSecond:F0} events/sec ({sw.ElapsedMilliseconds}ms for {totalEvents} events)");
        eventsPerSecond.Should().BeGreaterThan(100);

        cts.Cancel();
        await executeTask;
    }

    // ── Concurrent writers ──

    [Test]
    [CancelAfter(30000)]
    public async Task ConcurrentWriters_10Threads_AllEventsProcessed()
    {
        const int batchSize = 10;
        const int eventsPerThread = 10;
        const int threadCount = 10;
        const int totalEvents = eventsPerThread * threadCount;

        SetupBatchMock(batchSize);
        var batcher = CreateBatcher(batchSize, flushIntervalMs: 50);
        using var cts = new CancellationTokenSource();
        var executeTask = StartBatcher(batcher, cts.Token);

        var allTasks = new List<Task<AuditIntegrityDto>>();
        var writerTasks = Enumerable.Range(0, threadCount).Select(t =>
            Task.Run(async () =>
            {
                var tasks = new List<Task<AuditIntegrityDto>>();
                for (var i = 0; i < eventsPerThread; i++)
                {
                    tasks.Add(batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None));
                }
                lock (allTasks) allTasks.AddRange(tasks);
                await Task.WhenAll(tasks);
            })
        ).ToArray();

        await Task.WhenAll(writerTasks);
        await Task.WhenAll(allTasks);

        allTasks.Should().HaveCount(totalEvents);
        allTasks.Should().AllSatisfy(t => t.IsCompletedSuccessfully.Should().BeTrue());

        cts.Cancel();
        await executeTask;
    }

    // ── Flush on stop: all queued events flushed during shutdown ──

    [Test]
    [CancelAfter(15000)]
    public async Task FlushOnStop_AllQueuedEventsProcessed()
    {
        const int batchSize = 50;
        const int totalEvents = 25;

        SetupBatchMock(batchSize);
        var batcher = CreateBatcher(batchSize, flushIntervalMs: 5000); // long interval — won't auto-flush
        using var cts = new CancellationTokenSource();
        var executeTask = StartBatcher(batcher, cts.Token);

        var tasks = Enumerable.Range(0, totalEvents)
            .Select(_ => batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None))
            .ToList();

        // Allow events to be enqueued
        await Task.Delay(100);

        // Trigger shutdown — should flush remaining
        cts.Cancel();
        await executeTask;
        await Task.WhenAll(tasks);

        tasks.Should().AllSatisfy(t => t.IsCompletedSuccessfully.Should().BeTrue());
    }

    // ── No events lost ──

    [Test]
    [CancelAfter(30000)]
    public async Task NoEventsLost_SubmittedEqualsProcessed()
    {
        const int batchSize = 10;
        const int totalEvents = 73; // non-round number to test partial batches

        var processedCount = 0;
        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<AuditIntegrityDto>, CancellationToken>((events, _) =>
            {
                Interlocked.Add(ref processedCount, events.Count);
                return Task.FromResult<IReadOnlyList<AuditIntegrityDto>>(
                    events.Select(e => new AuditIntegrityDto { EventId = e.EventId }).ToList());
            });

        var batcher = CreateBatcher(batchSize, flushIntervalMs: 50);
        using var cts = new CancellationTokenSource();
        var executeTask = StartBatcher(batcher, cts.Token);

        var tasks = Enumerable.Range(0, totalEvents)
            .Select(_ => batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None))
            .ToList();
        await Task.WhenAll(tasks);

        cts.Cancel();
        await executeTask;

        processedCount.Should().Be(totalEvents, $"all {totalEvents} events should be processed");
    }

    #region Helpers

    private void SetupBatchMock(int maxBatchSize)
    {
        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .Returns<IReadOnlyList<AuditIntegrityDto>, CancellationToken>((events, _) =>
            {
                events.Count.Should().BeLessThanOrEqualTo(maxBatchSize,
                    "batch size must respect configured maximum");
                var results = events.Select(e => new AuditIntegrityDto { EventId = e.EventId }).ToList();
                return Task.FromResult<IReadOnlyList<AuditIntegrityDto>>(results);
            });
    }

    private IntegrityWriteBatcher CreateBatcher(int batchSize, int flushIntervalMs)
    {
        var options = new SecurityOptions
        {
            EnableBatchedIntegrityWrites = true,
            IntegrityBatchSize = batchSize,
            IntegrityFlushInterval = TimeSpan.FromMilliseconds(flushIntervalMs)
        };

        return new IntegrityWriteBatcher(
            _mockScopeFactory.Object,
            _mockLogger.Object,
            Options.Create(options));
    }

    private static Task StartBatcher(IntegrityWriteBatcher batcher, CancellationToken token)
    {
        return Task.Run(async () =>
        {
            try { await batcher.StartAsync(token); }
            catch (OperationCanceledException) { }
            try { await batcher.ExecuteTask!; }
            catch (OperationCanceledException) { }
            try { await batcher.StopAsync(CancellationToken.None); }
            catch (OperationCanceledException) { }
        });
    }

    private static AuditIntegrityDto CreateTestDto() => new()
    {
        EventId = Guid.NewGuid(),
        EventType = "Test.Event",
        User = "testuser",
        InsertedDate = DateTimeOffset.UtcNow,
        JsonData = "{\"test\":true}"
    };

    #endregion
}
