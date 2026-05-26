using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

[TestFixture]
[Category("Unit")]
public sealed class FileDlqOperationalBoundaryTests
{
    private string _testPath = null!;
    private Mock<ILogger<FileBasedAuditDeadLetterQueue>> _mockLogger = null!;
    private Mock<IAuditFieldRedactor> _mockRedactor = null!;
    private IServiceProvider _serviceProvider = null!;
    private IConfiguration _configuration = null!;

    [SetUp]
    public void SetUp()
    {
        _testPath = Path.Combine(Path.GetTempPath(), "AuditDLQ_BoundaryTest_" + Guid.NewGuid());
        _mockLogger = new Mock<ILogger<FileBasedAuditDeadLetterQueue>>();
        _mockRedactor = new Mock<IAuditFieldRedactor>();
        _mockRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(d => d);

        var services = new ServiceCollection();
        services.AddSingleton(_mockLogger.Object);
        _serviceProvider = services.BuildServiceProvider();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:DeadLetterQueue:Path"] = _testPath
            })
            .Build();
    }

    [TearDown]
    public void TearDown()
    {
        (_serviceProvider as IDisposable)?.Dispose();

        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }

    [Test]
    public async Task StoreFailedEvent_ExceedsSoftLimit_LogsWarning()
    {
        var options = new ResilienceOptions
        {
            FileBasedMaxQueueSize = 2,
            FileBasedHardCapacity = 100
        };

        var dlq = CreateDlq(options);

        // Store 2 events to reach the soft limit
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test1");
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test2");

        // Third event should trigger warning
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test3");

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => v.ToString()!.Contains("soft limit")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task StoreFailedEvent_ExceedsHardCapacity_Throws()
    {
        var options = new ResilienceOptions
        {
            FileBasedMaxQueueSize = 1,
            FileBasedHardCapacity = 3
        };

        var dlq = CreateDlq(options);

        // Fill up to hard cap
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test1");
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test2");
        await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test3");

        // Next event should be rejected
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: "test4");
        });
    }

    [Test]
    public async Task StoreFailedEvent_HardCapacityZero_NeverRejects()
    {
        var options = new ResilienceOptions
        {
            FileBasedMaxQueueSize = 1,
            FileBasedHardCapacity = 0 // Disabled
        };

        var dlq = CreateDlq(options);

        // Should not throw regardless of count
        for (int i = 0; i < 5; i++)
        {
            await dlq.StoreFailedEventAsync(CreateTestEvent(), reason: $"test{i}");
        }

        var stats = await dlq.GetStatisticsAsync();
        Assert.That(stats.TotalEvents, Is.EqualTo(5));
    }

    [Test]
    public void Constructor_NonWritablePath_Throws()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:DeadLetterQueue:Path"] = "/nonexistent/impossible/path_" + Guid.NewGuid()
            })
            .Build();

        // May throw IOException (read-only FS) or InvalidOperationException (write-probe failure)
        Assert.That(() =>
        {
            _ = new FileBasedAuditDeadLetterQueue(
                _mockLogger.Object,
                config,
                _serviceProvider,
                _mockRedactor.Object);
        }, Throws.InstanceOf<Exception>());
    }

    [Test]
    public void Constructor_MissingDirectory_CreatesDirectory()
    {
        var dlq = CreateDlq();

        Assert.That(Directory.Exists(_testPath), Is.True);
        _ = dlq;
    }

    [Test]
    public async Task GetStatisticsAsync_WithCorruptedFile_SkipsBadFile_AndLogsError()
    {
        var validEvent = CreateTestEvent();
        Directory.CreateDirectory(_testPath);
        await File.WriteAllTextAsync(Path.Combine(_testPath, "dlq_corrupted.json"), "{not-json");
        var dlq = CreateDlq();

        await dlq.StoreFailedEventAsync(validEvent, reason: "valid");

        var stats = await dlq.GetStatisticsAsync();

        Assert.That(stats.TotalEvents, Is.EqualTo(1));
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to index dead letter file")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    [Test]
    public async Task GetStatisticsAsync_DuringConcurrentEnqueue_RemainsConsistent()
    {
        var dlq = CreateDlq();

        var writers = Enumerable.Range(0, 10)
            .Select(i => dlq.StoreFailedEventAsync(CreateTestEvent(), reason: $"reason-{i}"))
            .ToArray();

        var readers = Enumerable.Range(0, 5)
            .Select(_ => dlq.GetStatisticsAsync())
            .ToArray();

        await Task.WhenAll(writers);
        var snapshots = await Task.WhenAll(readers);
        var finalStats = await dlq.GetStatisticsAsync();

        Assert.That(snapshots.All(s => s.TotalEvents >= 0), Is.True);
        Assert.That(finalStats.TotalEvents, Is.EqualTo(10));
        Assert.That(finalStats.PendingEvents + finalStats.ProcessedEvents + finalStats.FailedEvents, Is.EqualTo(10));
    }

    private FileBasedAuditDeadLetterQueue CreateDlq(ResilienceOptions? options = null)
    {
        return new FileBasedAuditDeadLetterQueue(
            _mockLogger.Object,
            _configuration,
            _serviceProvider,
            _mockRedactor.Object,
            options);
    }

    private static AuditEvent CreateTestEvent()
    {
        return new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?> { ["key"] = "value" }
        };
    }
}
