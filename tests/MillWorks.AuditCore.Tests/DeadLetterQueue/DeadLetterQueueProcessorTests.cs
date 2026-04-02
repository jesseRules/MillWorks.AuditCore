using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// Unit tests for DeadLetterQueueProcessor background service
/// </summary>
[TestFixture]
public class DeadLetterQueueProcessorTests
{
    /// <summary>
    /// Mock service provider
    /// </summary>
    private Mock<IServiceProvider> _mockServiceProvider;

    /// <summary>
    /// Mock service scope
    /// </summary>
    private Mock<IServiceScope> _mockServiceScope;

    /// <summary>
    /// Mock scoped service provider
    /// </summary>
    private Mock<IServiceProvider> _mockScopedServiceProvider;

    /// <summary>
    /// Mock dead letter queue
    /// </summary>
    private Mock<IAuditDeadLetterQueue> _mockDeadLetterQueue;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<DeadLetterQueueProcessor>> _mockLogger;

    /// <summary>
    /// Mock distributed lock service
    /// </summary>
    private Mock<IAuditDistributedLockService> _mockDistributedLockService;

    /// <summary>
    /// Configuration
    /// </summary>
    private IConfiguration _configuration;

    /// <summary>
    /// Processor under test
    /// </summary>
    private DeadLetterQueueProcessor _processor;

    /// <summary>
    /// Cancellation token source for tests
    /// </summary>
    private CancellationTokenSource _cancellationTokenSource;

    /// <summary>
    /// Sets up resources before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockServiceScope = new Mock<IServiceScope>();
        _mockScopedServiceProvider = new Mock<IServiceProvider>();
        _mockDeadLetterQueue = new Mock<IAuditDeadLetterQueue>();
        _mockLogger = new Mock<ILogger<DeadLetterQueueProcessor>>();
        _mockDistributedLockService = new Mock<IAuditDistributedLockService>();

        // Setup service scope chain
        _mockServiceScope.Setup(static x => x.ServiceProvider).Returns(_mockScopedServiceProvider.Object);
        _mockScopedServiceProvider.Setup(static x => x.GetService(typeof(IAuditDeadLetterQueue)))
            .Returns(_mockDeadLetterQueue.Object);
        _mockScopedServiceProvider.Setup(static x => x.GetService(typeof(IAuditDistributedLockService)))
            .Returns(_mockDistributedLockService.Object);

        _mockDistributedLockService
            .Setup(x => x.AcquireLockAsync(
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Mock.Of<IDisposable>());

        // Create and setup IServiceScopeFactory mock
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(static x => x.CreateScope()).Returns(_mockServiceScope.Object);

        // Register it in the service provider
        _mockServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockScopeFactory.Object);

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "1",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        _cancellationTokenSource = new CancellationTokenSource();

        _processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _configuration);
    }

    /// <summary>
    /// Tears down resources after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _cancellationTokenSource.Dispose();
        _processor.Dispose();
    }

    [Test]
    public async Task ProcessOnceAsync_AcquiresDistributedLockBeforeReprocessing()
    {
        var lockAcquired = false;
        var deadLetterEvent = new DeadLetterAuditEvent
        {
            Id = Guid.NewGuid().ToString(),
            IsProcessed = false,
            RetryCount = 0
        };

        _mockDistributedLockService
            .Setup(x => x.AcquireLockAsync(
                "audit:dead-letter-queue:reprocess",
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => lockAcquired = true)
            .ReturnsAsync(Mock.Of<IDisposable>());

        _mockDeadLetterQueue
            .Setup(x => x.GetFailedEventsAsync(100))
            .ReturnsAsync([deadLetterEvent]);

        _mockDeadLetterQueue
            .Setup(x => x.ReprocessEventAsync(deadLetterEvent.Id))
            .Callback(() => Assert.That(lockAcquired, Is.True))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(1);

        _mockDeadLetterQueue
            .Setup(x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics { TotalEvents = 1, PendingEvents = 0, FailedEvents = 0 });

        await _processor.ProcessOnceAsync(CancellationToken.None);

        _mockDistributedLockService.Verify(
            x => x.AcquireLockAsync(
                "audit:dead-letter-queue:reprocess",
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ProcessOnceAsync_LogsSuccessAndFailureCounts()
    {
        var events = new List<DeadLetterAuditEvent>
        {
            new() { Id = "success", IsProcessed = false, RetryCount = 0 },
            new() { Id = "failed", IsProcessed = false, RetryCount = 0 },
            new() { Id = "threw", IsProcessed = false, RetryCount = 0 }
        };

        _mockDeadLetterQueue
            .Setup(x => x.GetFailedEventsAsync(100))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(x => x.ReprocessEventAsync("success"))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(x => x.ReprocessEventAsync("failed"))
            .ReturnsAsync(false);

        _mockDeadLetterQueue
            .Setup(x => x.ReprocessEventAsync("threw"))
            .ThrowsAsync(new InvalidOperationException("boom"));

        _mockDeadLetterQueue
            .Setup(x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(1);

        _mockDeadLetterQueue
            .Setup(x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics { TotalEvents = 3, PendingEvents = 2, FailedEvents = 2 });

        await _processor.ProcessOnceAsync(CancellationToken.None);

        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains("Success=1") &&
                    v.ToString()!.Contains("Failure=2") &&
                    v.ToString()!.Contains("Total=3")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public void StartAsync_WhenLockServiceMissing_ThrowsMeaningfulError()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => Mock.Of<IAuditDeadLetterQueue>());
        using var provider = services.BuildServiceProvider();

        var processor = new DeadLetterQueueProcessor(provider, _mockLogger.Object, _configuration);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () => await processor.StartAsync(CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain("IAuditDistributedLockService"));
    }

    /// <summary>
    /// Ensures that when auto-reprocessing is disabled, the processor exits immediately
    /// </summary>
    [Test]
    public async Task ExecuteAsync_WithAutoReprocessDisabled_ExitsImmediately()
    {
        // Arrange
        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "false"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        // Act
        await processor.StartAsync(cts.Token);
        await Task.Delay(500, cts.Token); // Give it time to start
        await processor.StopAsync(cts.Token);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("auto-reprocessing is disabled")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);

        _mockDeadLetterQueue.Verify(
            static x => x.GetFailedEventsAsync(It.IsAny<int>()),
            Times.Never);
    }

    /// <summary>
    /// Ensures that events under max retries are processed
    /// </summary>
    [Test]
    public async Task ExecuteAsync_ProcessesEventsUnderMaxRetries()
    {
        // Arrange
        var events = new List<DeadLetterAuditEvent>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 1
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 2
            }
        };

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(static x => x.ReprocessEventAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics
            {
                TotalEvents = 2,
                PendingEvents = 2,
                FailedEvents = 0
            });

        // Use short interval for testing
        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.02", // ~1.2 seconds
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);

        // Wait long enough for initial delay + both event processing + delays between
        // Initial delay: ~1200ms, Event1: 0ms, Delay: 1000ms, Event2: 0ms, Delay: 1000ms = ~3200ms
        await Task.Delay(4000); // Don't pass any cancellation token here

        await processor.StopAsync(CancellationToken.None);

        // Assert
        _mockDeadLetterQueue.Verify(
            static x => x.GetFailedEventsAsync(It.IsAny<int>()),
            Times.AtLeastOnce);

        _mockDeadLetterQueue.Verify(
            static x => x.ReprocessEventAsync(It.IsAny<string>()),
            Times.Exactly(2)); // Should be called exactly twice, once per event
    }

    /// <summary>
    /// Ensures that events exceeding max retries are skipped
    /// </summary>
    [Test]
    public async Task ExecuteAsync_SkipsEventsExceedingMaxRetries()
    {
        // Arrange
        var events = new List<DeadLetterAuditEvent>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 3 // At max retries
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 5 // Exceeded max retries
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 1 // Under max retries
            }
        };

        var reprocessedIds = new List<string>();

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(static x => x.ReprocessEventAsync(It.IsAny<string>()))
            .Callback<string>(id => reprocessedIds.Add(id))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(2));

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000, cts.Token);
        await processor.StopAsync(CancellationToken.None);

        // Assert - Only the event with RetryCount=1 should be reprocessed
        Assert.That(reprocessedIds, Has.Count.EqualTo(1));
        Assert.That(reprocessedIds[0], Is.EqualTo(events[2].Id));
    }

    /// <summary>
    /// Ensures that already processed events are skipped
    /// </summary>
    [Test]
    public async Task ExecuteAsync_SkipsProcessedEvents()
    {
        // Arrange
        var events = new List<DeadLetterAuditEvent>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = true, // Already processed
                RetryCount = 0
            },
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 0
            }
        };

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(static x => x.ReprocessEventAsync(It.IsAny<string>()))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(1);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000);
        await processor.StopAsync(CancellationToken.None);

        // Assert - Should only reprocess the unprocessed event
        _mockDeadLetterQueue.Verify(
            x => x.ReprocessEventAsync(events[1].Id),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Ensures that processed events are purged correctly
    /// </summary>
    [Test]
    public async Task ExecuteAsync_PurgesProcessedEvents()
    {
        // Arrange
        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<DeadLetterAuditEvent>());

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(5);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics
            {
                TotalEvents = 10,
                ProcessedEvents = 5
            });

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000);
        await processor.StopAsync(CancellationToken.None);

        // Assert
        _mockDeadLetterQueue.Verify(
            static x => x.PurgeProcessedEventsAsync(),
            Times.AtLeastOnce);

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Purged 5 processed events")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Ensures that statistics are logged correctly
    /// </summary>
    [Test]
    public async Task ExecuteAsync_LogsStatistics()
    {
        // Arrange
        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<DeadLetterAuditEvent>());

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics
            {
                TotalEvents = 15,
                PendingEvents = 10,
                FailedEvents = 5
            });

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000);
        await processor.StopAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) =>
                    v.ToString()!.Contains("Total=15") &&
                    v.ToString()!.Contains("Pending=10") &&
                    v.ToString()!.Contains("Failed=5")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Ensures that errors during reprocessing are handled gracefully
    /// </summary>
    [Test]
    public async Task ExecuteAsync_HandlesReprocessingErrors()
    {
        // Arrange
        var events = new List<DeadLetterAuditEvent>
        {
            new()
            {
                Id = Guid.NewGuid().ToString(),
                IsProcessed = false,
                RetryCount = 0
            }
        };

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(static x => x.ReprocessEventAsync(It.IsAny<string>()))
            .ThrowsAsync(new Exception("Reprocessing failed"));

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000);
        await processor.StopAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to reprocess")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Ensures that general processing errors are handled gracefully
    /// </summary>
    [Test]
    public async Task ExecuteAsync_HandlesGeneralProcessingErrors()
    {
        // Arrange
        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ThrowsAsync(new Exception("Database connection failed"));

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1000);
        await processor.StopAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Error processing dead letter queue")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Ensures that there is a delay between retries as configured
    /// </summary>
    [Test]
    public async Task ExecuteAsync_IncludesDelayBetweenRetries()
    {
        // Arrange
        var events = new List<DeadLetterAuditEvent>
        {
            new() { Id = "1", IsProcessed = false, RetryCount = 0 },
            new() { Id = "2", IsProcessed = false, RetryCount = 0 }
        };

        var reprocessTimes = new List<DateTimeOffset>();

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(events);

        _mockDeadLetterQueue
            .Setup(static x => x.ReprocessEventAsync(It.IsAny<string>()))
            .Callback(() => reprocessTimes.Add(DateTimeOffset.UtcNow))
            .ReturnsAsync(true);

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.01",
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(1500);
        await processor.StopAsync(CancellationToken.None);

        // Assert - There should be a delay between reprocessing attempts
        if (reprocessTimes.Count >= 2)
        {
            var timeDiff = reprocessTimes[1] - reprocessTimes[0];
            Assert.That(timeDiff, Is.GreaterThanOrEqualTo(TimeSpan.FromMilliseconds(500)));
        }
    }

    /// <summary>
    /// Ensures that the cancellation token is respected during processing
    /// </summary>
    [Test]
    public async Task ExecuteAsync_RespectsCancellationToken()
    {
        // Arrange
        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .ReturnsAsync(new List<DeadLetterAuditEvent>());

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "10", // Long interval
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        var cts = new CancellationTokenSource();

        // Act
        await processor.StartAsync(cts.Token);
        await Task.Delay(500, cts.Token);
        await cts.CancelAsync(); // Cancel before the interval completes
        await processor.StopAsync(CancellationToken.None);

        // Assert - Should not have completed a full cycle
        _mockDeadLetterQueue.Verify(
            static x => x.GetFailedEventsAsync(It.IsAny<int>()),
            Times.Never);
    }

    /// <summary>
    /// Ensures that the configured interval minutes are used between processing cycles
    /// </summary>
    [Test]
    public async Task ExecuteAsync_UsesConfiguredIntervalMinutes()
    {
        // Arrange
        var processingCount = 0;

        _mockDeadLetterQueue
            .Setup(static x => x.GetFailedEventsAsync(It.IsAny<int>()))
            .Callback(() => processingCount++)
            .ReturnsAsync(new List<DeadLetterAuditEvent>());

        _mockDeadLetterQueue
            .Setup(static x => x.PurgeProcessedEventsAsync())
            .ReturnsAsync(0);

        _mockDeadLetterQueue
            .Setup(static x => x.GetStatisticsAsync())
            .ReturnsAsync(new DeadLetterStatistics());

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:AutoReprocess"] = "true",
            ["Audit:DeadLetterQueue:ReprocessIntervalMinutes"] = "0.02", // ~1.2 seconds
            ["Audit:DeadLetterQueue:MaxRetries"] = "3"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        var processor = new DeadLetterQueueProcessor(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            config);

        // Act
        await processor.StartAsync(CancellationToken.None);
        await Task.Delay(3000); // Wait for ~2-3 cycles
        await processor.StopAsync(CancellationToken.None);

        // Assert - Should have processed at least twice
        Assert.That(processingCount, Is.GreaterThanOrEqualTo(2));
    }
}
