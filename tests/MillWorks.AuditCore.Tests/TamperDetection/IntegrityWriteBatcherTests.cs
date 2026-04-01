using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.TamperDetection;

[TestFixture]
[Category("Unit")]
public sealed class IntegrityWriteBatcherTests
{
    private Mock<IServiceScopeFactory> _mockScopeFactory = null!;
    private Mock<IServiceScope> _mockScope = null!;
    private Mock<IServiceProvider> _mockScopeProvider = null!;
    private Mock<ITamperDetectionService> _mockTamperDetection = null!;
    private Mock<ILogger<IntegrityWriteBatcher>> _mockLogger = null!;

    [SetUp]
    public void SetUp()
    {
        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeProvider = new Mock<IServiceProvider>();
        _mockTamperDetection = new Mock<ITamperDetectionService>();
        _mockLogger = new Mock<ILogger<IntegrityWriteBatcher>>();

        _mockScopeFactory.Setup(f => f.CreateScope()).Returns(_mockScope.Object);
        _mockScope.Setup(s => s.ServiceProvider).Returns(_mockScopeProvider.Object);
        _mockScopeProvider.Setup(p => p.GetService(typeof(ITamperDetectionService)))
            .Returns(_mockTamperDetection.Object);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task EnqueueAsync_SingleEvent_FlushesAndReturnsResult()
    {
        var eventDto = CreateTestDto();
        var resultDto = new AuditIntegrityDto { EventId = eventDto.EventId };

        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditIntegrityDto> { resultDto });

        var batcher = CreateBatcher(batchSize: 1, flushIntervalMs: 100);
        using var cts = new CancellationTokenSource();

        // Start the background service
        var executeTask = StartBatcher(batcher, cts.Token);

        var result = await batcher.EnqueueAsync(eventDto, CancellationToken.None);

        Assert.That(result.EventId, Is.EqualTo(eventDto.EventId));

        cts.Cancel();
        await executeTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task EnqueueAsync_BatchOfEvents_FlushesOnBatchSize()
    {
        var events = Enumerable.Range(0, 5).Select(_ => CreateTestDto()).ToList();

        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditIntegrityDto> e, CancellationToken _) =>
                e.Select(x => new AuditIntegrityDto { EventId = x.EventId }).ToList());

        var batcher = CreateBatcher(batchSize: 5, flushIntervalMs: 5000);
        using var cts = new CancellationTokenSource();

        var executeTask = StartBatcher(batcher, cts.Token);

        // Enqueue all 5 events concurrently
        var tasks = events.Select(e => batcher.EnqueueAsync(e, CancellationToken.None)).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.That(results, Has.Length.EqualTo(5));
        foreach (var r in results)
        {
            Assert.That(r.EventId, Is.Not.EqualTo(Guid.Empty));
        }

        cts.Cancel();
        await executeTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task EnqueueAsync_FlushFailure_PropagatesException()
    {
        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB error"));

        var batcher = CreateBatcher(batchSize: 1, flushIntervalMs: 100);
        using var cts = new CancellationTokenSource();

        var executeTask = StartBatcher(batcher, cts.Token);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None);
        });

        cts.Cancel();
        await executeTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task FlushBatch_ResultCountMismatch_SignalsFailureToAllCallers()
    {
        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditIntegrityDto>()); // Returns 0 results for 1 enqueued

        var batcher = CreateBatcher(batchSize: 1, flushIntervalMs: 100);
        using var cts = new CancellationTokenSource();

        var executeTask = StartBatcher(batcher, cts.Token);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None);
        });

        cts.Cancel();
        await executeTask;
    }

    [Test]
    [CancelAfter(10000)]
    public async Task Shutdown_FlushesRemainingRecords()
    {
        int flushCallCount = 0;

        _mockTamperDetection
            .Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditIntegrityDto> events, CancellationToken _) =>
            {
                Interlocked.Increment(ref flushCallCount);
                return events.Select(e => new AuditIntegrityDto { EventId = e.EventId }).ToList();
            });

        // Large batch size + long flush interval so nothing flushes during normal operation
        var batcher = CreateBatcher(batchSize: 100, flushIntervalMs: 60000);
        using var cts = new CancellationTokenSource();

        var executeTask = StartBatcher(batcher, cts.Token);

        // Enqueue one event — won't flush yet (batch not full, interval not elapsed)
        var enqueueTask = batcher.EnqueueAsync(CreateTestDto(), CancellationToken.None);

        // Cancel triggers shutdown flush
        cts.Cancel();
        await executeTask;

        // The enqueue should complete after shutdown flush
        var result = await enqueueTask;
        Assert.That(result.EventId, Is.Not.EqualTo(Guid.Empty));

        // At least one flush call should have happened (either during normal loop or shutdown)
        Assert.That(flushCallCount, Is.GreaterThanOrEqualTo(1));
    }

    private IntegrityWriteBatcher CreateBatcher(int batchSize = 50, int flushIntervalMs = 500)
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
            options);
    }

    private static async Task StartBatcher(IntegrityWriteBatcher batcher, CancellationToken token)
    {
        await batcher.StartAsync(token);
        // ExecuteAsync runs in the background via BackgroundService; give it a moment to start
        await Task.Delay(50);
    }

    private static AuditIntegrityDto CreateTestDto()
    {
        return new AuditIntegrityDto
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{}",
            User = "TestUser"
        };
    }
}
