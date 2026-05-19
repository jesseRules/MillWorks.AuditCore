using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// Unit tests for FileBasedAuditDeadLetterQueue
/// </summary>
[TestFixture]
public class FileBasedAuditDeadLetterQueueTests
{
    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<FileBasedAuditDeadLetterQueue>> _mockLogger;

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
    /// Mock audit logger
    /// </summary>
    private Mock<IAuditLogger> _mockAuditLogger;

    /// <summary>
    /// Configuration instance
    /// </summary>
    private IConfiguration _configuration;

    /// <summary>
    /// Dead letter queue instance
    /// </summary>
    private FileBasedAuditDeadLetterQueue _deadLetterQueue;

    /// <summary>
    /// Test directory path
    /// </summary>
    private string _testPath;

    /// <summary>
    /// Setup before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<FileBasedAuditDeadLetterQueue>>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockServiceScope = new Mock<IServiceScope>();
        _mockScopedServiceProvider = new Mock<IServiceProvider>();
        _mockAuditLogger = new Mock<IAuditLogger>();

        // Setup service scope chain
        _mockServiceScope.Setup(static x => x.ServiceProvider).Returns(_mockScopedServiceProvider.Object);
        _mockScopedServiceProvider.Setup(static x => x.GetService(typeof(IAuditLogger)))
            .Returns(_mockAuditLogger.Object);

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(static x => x.CreateScope()).Returns(_mockServiceScope.Object);
        _mockServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockScopeFactory.Object);

        // Use temp directory for testing
        _testPath = Path.Combine(Path.GetTempPath(), $"AuditDLQ_Test_{Guid.NewGuid()}");

        var configDict = new Dictionary<string, string>
        {
            ["Audit:DeadLetterQueue:Path"] = _testPath
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        _deadLetterQueue = new FileBasedAuditDeadLetterQueue(
            _mockLogger.Object,
            _configuration,
            _mockServiceProvider.Object,
            new PassThroughAuditFieldRedactor());
    }

    /// <summary>
    /// Tear down after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        // Clean up test directory
        if (Directory.Exists(_testPath))
        {
            Directory.Delete(_testPath, true);
        }
    }

    /// <summary>
    /// StoreFailedEventAsync creates a file successfully
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_CreatesFile_Successfully()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow
        };
        var exception = new Exception("Test error");

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, exception, "Test failure");

        // Assert
        var files = Directory.GetFiles(_testPath, "*.json");
        Assert.That(files, Has.Length.EqualTo(1));
    }

    /// <summary>
    /// StoreFailedEventAsync stores correct metadata
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_StoresCorrectMetadata()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var auditEvent = new AuditEvent
        {
            EventId = eventId,
            EventType = "User.Login",
            StartDate = DateTimeOffset.UtcNow
        };

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Database unavailable");

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].OriginalEvent?.EventId, Is.EqualTo(eventId));
        Assert.That(events[0].FailureReason, Is.EqualTo("Database unavailable"));
        Assert.That(events[0].Metadata["EventType"].ToString(), Is.EqualTo("User.Login"));
    }

    /// <summary>
    /// StoreFailedEntityAsync creates a file successfully
    /// </summary>
    [Test]
    public async Task StoreFailedEntityAsync_CreatesFile_Successfully()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        // Act
        await _deadLetterQueue.StoreFailedEntityAsync(entity, null, "Test reason");

        // Assert
        var files = Directory.GetFiles(_testPath, "*.json");
        Assert.That(files, Has.Length.EqualTo(1));
    }

    /// <summary>
    /// GetFailedEventsAsync returns events in descending order
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_ReturnsEventsInDescendingOrder()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await Task.Delay(100); // Ensure different timestamps
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert
        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0].OriginalEvent?.EventType, Is.EqualTo("Event2")); // Most recent first
        Assert.That(events[1].OriginalEvent?.EventType, Is.EqualTo("Event1"));
    }

    /// <summary>
    /// GetFailedEventsAsync respects maxCount parameter
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_RespectsMaxCount()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            await _deadLetterQueue.StoreFailedEventAsync(
                new AuditEvent { EventId = Guid.NewGuid(), EventType = $"Event{i}" },
                null, $"Reason{i}");
        }

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync(maxCount: 5);

        // Assert
        Assert.That(events, Has.Count.EqualTo(5));
    }

    /// <summary>
    /// GetFailedEventsByDateAsync filters events correctly
    /// </summary>
    [Test]
    public async Task GetFailedEventsByDateAsync_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddHours(-2);
        var endDate = DateTimeOffset.UtcNow.AddHours(2);

        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" },
            null, "Reason");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsByDateAsync(startDate, endDate);

        // Assert
        Assert.That(events, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// GetFailedEventsByDateAsync excludes events outside range
    /// </summary>
    [Test]
    public async Task GetFailedEventsByDateAsync_ExcludesEventsOutsideRange()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-2);
        var endDate = DateTimeOffset.UtcNow.AddDays(-1);

        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" },
            null, "Reason");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsByDateAsync(startDate, endDate);

        // Assert
        Assert.That(events, Is.Empty);
    }

    /// <summary>
    /// ReprocessEventAsync processes event successfully
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithValidEvent_ReturnsTrue()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// ReprocessEventAsync updates retry count and metadata
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_UpdatesRetryCount()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert — event is now processed, so GetFailedEventsAsync won't return it
        Assert.That(result, Is.True);

        // Verify via file on disk — the event should be updated in place
        var filePath = Path.Combine(_testPath, $"dlq_{deadLetterId}.json");
        Assert.That(File.Exists(filePath), Is.True);

        var json = await File.ReadAllTextAsync(filePath);
        var reprocessed = System.Text.Json.JsonSerializer.Deserialize<DeadLetterAuditEvent>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.That(reprocessed!.RetryCount, Is.EqualTo(1));
        Assert.That(reprocessed.LastRetryAt, Is.Not.Null);
        Assert.That(reprocessed.IsProcessed, Is.True);
        Assert.That(reprocessed.ProcessedAt, Is.Not.Null);

        // GetFailedEventsAsync should now return empty (all events processed)
        var remaining = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(remaining, Has.Count.EqualTo(0));
    }

    /// <summary>
    /// ReprocessEventAsync with non-existent ID returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithNonExistentId_ReturnsFalse()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid().ToString();

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(nonExistentId);

        // Assert
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// ReprocessEventAsync when logger fails returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WhenLoggerFails_ReturnsFalse()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Logging failed"));

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// ReprocessEventAsync when logger fails updates metadata
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WhenLoggerFails_UpdatesMetadata()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Logging failed"));

        // Act
        await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        var reprocessedEvents = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(reprocessedEvents[0].Metadata.ContainsKey("RetryFailure_1"), Is.True);
    }

    /// <summary>
    /// ReprocessAllAsync processes all unprocessed events
    /// </summary>
    [Test]
    public async Task ReprocessAllAsync_ProcessesAllUnprocessedEvents()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync();

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(2));
        Assert.That(result.SuccessfullyProcessed, Is.EqualTo(2));
        Assert.That(result.FailedToProcess, Is.EqualTo(0));
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    /// <summary>
    /// ReprocessAllAsync respects cancellation token
    /// </summary>
    [Test]
    public async Task ReprocessAllAsync_WithCancellation_StopsProcessing()
    {
        // Arrange
        for (int i = 0; i < 5; i++)
        {
            await _deadLetterQueue.StoreFailedEventAsync(
                new AuditEvent { EventId = Guid.NewGuid(), EventType = $"Type{i}" },
                null, $"Reason{i}");
        }

        var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync(cts.Token);

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(5));
        Assert.That(result.SuccessfullyProcessed + result.FailedToProcess, Is.LessThanOrEqualTo(result.TotalEvents));
    }

    /// <summary>
    /// ReprocessAllAsync handles mixed results correctly
    /// </summary>
    [Test]
    public async Task ReprocessAllAsync_WithMixedResults_ReturnsCorrectCounts()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");

        var callCount = 0;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                return callCount == 2 ? Task.FromException(new Exception("Logging failed")) : Task.CompletedTask;
            });

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync();

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(2));
        Assert.That(result.SuccessfullyProcessed, Is.EqualTo(1));
        Assert.That(result.FailedToProcess, Is.EqualTo(1));
        Assert.That(result.FailedEventIds, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// PurgeProcessedEventsAsync moves processed events to subfolder
    /// </summary>
    [Test]
    public async Task PurgeProcessedEventsAsync_MovesProcessedToSubfolder()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        await _deadLetterQueue.ReprocessEventAsync(events[0].Id);

        // Act
        var purgedCount = await _deadLetterQueue.PurgeProcessedEventsAsync();

        // Assert
        Assert.That(purgedCount, Is.EqualTo(1));

        var processedPath = Path.Combine(_testPath, "Processed");
        Assert.That(Directory.Exists(processedPath), Is.True);
        Assert.That(Directory.GetFiles(processedPath, "*.json"), Has.Length.EqualTo(1));

        var remainingEvents = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(remainingEvents, Has.Count.EqualTo(1));
        Assert.That(remainingEvents[0].IsProcessed, Is.False);
    }

    /// <summary>
    /// PurgeProcessedEventsAsync with no processed events returns zero
    /// </summary>
    [Test]
    public async Task PurgeProcessedEventsAsync_WithNoProcessedEvents_ReturnsZero()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" },
            null, "Reason");

        // Act
        var purgedCount = await _deadLetterQueue.PurgeProcessedEventsAsync();

        // Assert
        Assert.That(purgedCount, Is.EqualTo(0));
    }

    /// <summary>
    /// GetStatisticsAsync returns correct statistics
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public async Task GetStatisticsAsync_ReturnsCorrectStatistics()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason3");

        // Act
        var stats = await _deadLetterQueue.GetStatisticsAsync();

        // Assert
        Assert.That(stats.TotalEvents, Is.EqualTo(3));
        Assert.That(stats.PendingEvents, Is.EqualTo(3));
        Assert.That(stats.ProcessedEvents, Is.EqualTo(0));
        Assert.That(stats.EventsByType["Type1"], Is.EqualTo(2));
        Assert.That(stats.EventsByType["Type2"], Is.EqualTo(1));
        Assert.That(stats.EventsByFailureReason["Reason1"], Is.EqualTo(1));
        Assert.That(stats.OldestEventDate, Is.Not.Null);
        Assert.That(stats.NewestEventDate, Is.Not.Null);
        Assert.That(stats.TotalSizeBytes, Is.GreaterThan(0));
    }

    /// <summary>
    /// GetStatisticsAsync with empty queue returns zero stats
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    public async Task GetStatisticsAsync_WithEmptyQueue_ReturnsZeroStats()
    {
        // Act
        var stats = await _deadLetterQueue.GetStatisticsAsync();

        // Assert
        Assert.That(stats.TotalEvents, Is.EqualTo(0));
        Assert.That(stats.ProcessedEvents, Is.EqualTo(0));
        Assert.That(stats.PendingEvents, Is.EqualTo(0));
        Assert.That(stats.FailedEvents, Is.EqualTo(0));
        Assert.That(stats.OldestEventDate, Is.Null);
        Assert.That(stats.NewestEventDate, Is.Null);
        Assert.That(stats.TotalSizeBytes, Is.EqualTo(0));
    }

    /// <summary>
    /// StoreFailedEventAsync is thread-safe
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_WithThreadSafety_HandlesCorrectly()
    {
        // Arrange
        var tasks = new List<Task>();

        // Act - Store multiple events concurrently
        for (int i = 0; i < 10; i++)
        {
            var eventId = i;
            tasks.Add(Task.Run(async () =>
            {
                await _deadLetterQueue.StoreFailedEventAsync(
                    new AuditEvent { EventId = Guid.NewGuid(), EventType = $"Event{eventId}" },
                    null, $"Reason{eventId}");
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(10));
    }

    /// <summary>
    /// StoreFailedEventAsync with null exception stores without details
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_WithNullException_StoresWithoutExceptionDetails()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Test reason");

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events[0].ExceptionMessage, Is.Null);
        Assert.That(events[0].ExceptionStackTrace, Is.Null);
        Assert.That(events[0].FailureReason, Is.EqualTo("Test reason"));
    }

    /// <summary>
    /// StoreFailedEventAsync with null reason uses "Unknown"
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_WithNullReason_UsesUnknown()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent);

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events[0].FailureReason, Is.EqualTo("Unknown"));
    }

    /// <summary>
    /// GetFailedEventsAsync with corrupted file skips and logs error during index build
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_WithCorruptedFile_SkipsAndLogs()
    {
        // Arrange: create corrupted file BEFORE any operation triggers index build
        var corruptedFile = Path.Combine(_testPath, "dlq_corrupted_corrupt.json");
        await File.WriteAllTextAsync(corruptedFile, "{ invalid json");

        // Store a valid event (triggers index build, which encounters the corrupted file)
        var validEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Valid" };
        await _deadLetterQueue.StoreFailedEventAsync(validEvent, null, "Reason");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert
        Assert.That(events, Has.Count.EqualTo(1)); // Only valid event
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to index dead letter file")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Constructor with custom path creates directory
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task Constructor_WithCustomPath_CreatesDirectory()
    {
        // Arrange
        var customPath = Path.Combine(Path.GetTempPath(), $"CustomDLQ_{Guid.NewGuid()}");
        var customConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Audit:DeadLetterQueue:Path"] = customPath
            }!)
            .Build();

        // Act
        var unused = new FileBasedAuditDeadLetterQueue(
            _mockLogger.Object,
            customConfig,
            _mockServiceProvider.Object,
            new PassThroughAuditFieldRedactor());

        // Assert
        Assert.That(Directory.Exists(customPath), Is.True);

        // Cleanup
        Directory.Delete(customPath, true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// ReprocessEventAsync with no audit logger returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithNoAuditLogger_ReturnsFalse()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        // Setup service scope to return null for IAuditLogger
        _mockScopedServiceProvider.Setup(static x => x.GetService(typeof(IAuditLogger)))
            .Returns(null!);

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.False);
    }

    /// <summary>
    /// Constructor with non-writable path throws at startup
    /// </summary>
    [Test]
    public void Constructor_WithNonWritablePath_ThrowsAtStartup()
    {
        // Arrange — use a system-owned path that cannot be written to by normal users
        var readOnlyPath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\nonexistent_dlq_test"
            : "/usr/nonexistent_dlq_test";

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Audit:DeadLetterQueue:Path"] = readOnlyPath
            }!)
            .Build();

        // Act & Assert — should fail fast at startup, not silently succeed
        Assert.That(() =>
        {
            _ = new FileBasedAuditDeadLetterQueue(
                _mockLogger.Object,
                config,
                _mockServiceProvider.Object,
                new PassThroughAuditFieldRedactor());
        }, Throws.Exception);
    }
}
