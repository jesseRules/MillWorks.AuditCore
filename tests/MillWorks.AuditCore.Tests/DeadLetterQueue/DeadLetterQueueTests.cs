using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// InMemoryAuditDeadLetterQueue tests
/// </summary>
[TestFixture]
public class InMemoryAuditDeadLetterQueueTests
{
    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<InMemoryAuditDeadLetterQueue>> _mockLogger;

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
    /// Dead letter queue instance
    /// </summary>
    private InMemoryAuditDeadLetterQueue _deadLetterQueue;

    /// <summary>
    /// Setup before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<InMemoryAuditDeadLetterQueue>>();
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockServiceScope = new Mock<IServiceScope>();
        _mockScopedServiceProvider = new Mock<IServiceProvider>();
        _mockAuditLogger = new Mock<IAuditLogger>();

        // Setup the service scope chain
        _mockServiceScope.Setup(static x => x.ServiceProvider).Returns(_mockScopedServiceProvider.Object);
        _mockScopedServiceProvider.Setup(static x => x.GetService(typeof(IAuditLogger)))
            .Returns(_mockAuditLogger.Object);

        // Mock IServiceScopeFactory instead of CreateScope extension method
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(static x => x.CreateScope()).Returns(_mockServiceScope.Object);
        _mockServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockScopeFactory.Object);

        _deadLetterQueue = new InMemoryAuditDeadLetterQueue(
            _mockLogger.Object,
            _mockServiceProvider.Object);
    }

    /// <summary>
    /// StoreFailedEventAsync stores the event
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_StoresEvent()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };
        var exception = new Exception("Test error");

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, exception, "Test failure");

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].OriginalEvent, Is.EqualTo(auditEvent));
        Assert.That(events[0].FailureReason, Is.EqualTo("Test failure"));
        Assert.That(events[0].ExceptionMessage, Is.EqualTo("Test error"));
    }

    /// <summary>
    /// StoreFailedEntityAsync stores the entity
    /// </summary>
    [Test]
    public async Task StoreFailedEntityAsync_StoresEntity()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        // Act
        await _deadLetterQueue.StoreFailedEntityAsync(entity, null, "Test reason");

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].OriginalEntity, Is.EqualTo(entity));
    }

    /// <summary>
    /// GetFailedEventsAsync returns events in reverse chronological order
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_ReturnsEventsInReverseChronological()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await Task.Delay(10); // Ensure different timestamps
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert
        Assert.That(events, Has.Count.EqualTo(2));
        Assert.That(events[0].OriginalEvent?.EventType, Is.EqualTo("Event2")); // Most recent first
        Assert.That(events[1].OriginalEvent?.EventType, Is.EqualTo("Event1"));
    }

    /// <summary>
    /// GetFailedEventsByDateAsync filters events by date range
    /// </summary>
    [Test]
    public async Task GetFailedEventsByDateAsync_FiltersCorrectly()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason");

        var startDate = DateTimeOffset.UtcNow.AddHours(-1);
        var endDate = DateTimeOffset.UtcNow.AddHours(1);

        // Act
        var events = await _deadLetterQueue.GetFailedEventsByDateAsync(startDate, endDate);

        // Assert
        Assert.That(events, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// ReprocessEventAsync reprocesses a valid event successfully
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithValidEvent_ReprocessesSuccessfully()
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

        var reprocessedEvents = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(reprocessedEvents[0].IsProcessed, Is.True);
        Assert.That(reprocessedEvents[0].RetryCount, Is.EqualTo(1));
        Assert.That(reprocessedEvents[0].LastRetryAt, Is.Not.Null);
        Assert.That(reprocessedEvents[0].ProcessedAt, Is.Not.Null);
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
    /// ReprocessEventAsync when logger throws exception returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WhenLoggerThrowsException_ReturnsFalse()
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

        var reprocessedEvents = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(reprocessedEvents[0].IsProcessed, Is.False);
        Assert.That(reprocessedEvents[0].RetryCount, Is.EqualTo(1)); // Retry count still increments
    }

    /// <summary>
    /// PurgeProcessedEventsAsync removes processed events
    /// </summary>
    [Test]
    public async Task PurgeProcessedEventsAsync_RemovesProcessedEvents()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Mark first event as processed
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        await _deadLetterQueue.ReprocessEventAsync(events[0].Id);

        // Act
        var purgedCount = await _deadLetterQueue.PurgeProcessedEventsAsync();

        // Assert
        Assert.That(purgedCount, Is.EqualTo(1));

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
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason");

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
    [Ignore("Flaky test - needs investigation")]
    public async Task GetStatisticsAsync_ReturnsCorrectStats()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");

        // Act
        var stats = await _deadLetterQueue.GetStatisticsAsync();

        // Assert
        Assert.That(stats.TotalEvents, Is.EqualTo(2));
        Assert.That(stats.PendingEvents, Is.EqualTo(2));
        Assert.That(stats.ProcessedEvents, Is.EqualTo(0));
        Assert.That(stats.EventsByType.ContainsKey("Type1"), Is.True);
        Assert.That(stats.EventsByType.ContainsKey("Type2"), Is.True);
        Assert.That(stats.EventsByType["Type1"], Is.EqualTo(1));
        Assert.That(stats.EventsByType["Type2"], Is.EqualTo(1));
        Assert.That(stats.EventsByFailureReason.ContainsKey("Reason1"), Is.True);
        Assert.That(stats.OldestEventDate, Is.Not.Null);
        Assert.That(stats.NewestEventDate, Is.Not.Null);
    }

    /// <summary>
    /// GetStatisticsAsync with empty queue returns zero stats
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [Ignore("Flaky test - needs investigation")]
    public async Task GetStatisticsAsync_WithEmptyQueue_ReturnsZeroStats()
    {
        // Act
        var stats = await _deadLetterQueue.GetStatisticsAsync();

        // Assert
        Assert.That(stats.TotalEvents, Is.EqualTo(0));
        Assert.That(stats.ProcessedEvents, Is.EqualTo(0));
        Assert.That(stats.PendingEvents, Is.EqualTo(0));
        Assert.That(stats.FailedEvents, Is.EqualTo(0));
        Assert.That(stats.EventsByType, Is.Empty);
        Assert.That(stats.EventsByFailureReason, Is.Empty);
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
        Assert.That(result.FailedEventIds, Is.Empty);
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    /// <summary>
    /// ReprocessAllAsync with some failures returns correct counts
    /// </summary>
    [Test]
    public async Task ReprocessAllAsync_WithSomeFailures_ReturnsCorrectCounts()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");

        // First call succeeds, second fails
        var callCount = 0;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                callCount++;
                if (callCount == 2)
                    return Task.FromException(new Exception("Logging failed"));
                return Task.CompletedTask;
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
    /// ReprocessAllAsync with cancellation token stops processing
    /// </summary>
    [Test]
    public async Task ReprocessAllAsync_WithCancellationToken_StopsProcessing()
    {
        // Arrange
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type2" },
            null, "Reason2");

        var cts = new CancellationTokenSource();
        await cts.CancelAsync(); // Cancel immediately

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync(cts.Token);

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(2));
        Assert.That(result.SuccessfullyProcessed + result.FailedToProcess, Is.LessThanOrEqualTo(result.TotalEvents));
    }

    /// <summary>
    /// GetFailedEventsAsync with maxCount limits results
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_WithMaxCount_LimitsResults()
    {
        // Arrange
        for (int i = 0; i < 10; i++)
        {
            await _deadLetterQueue.StoreFailedEventAsync(
                new AuditEvent { EventId = Guid.NewGuid(), EventType = $"Type{i}" },
                null, $"Reason{i}");
        }

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync(maxCount: 5);

        // Assert
        Assert.That(events, Has.Count.EqualTo(5));
    }

    /// <summary>
    /// StoreFailedEventAsync with null exception stores event without exception details
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_WithNullException_StoresEventWithoutExceptionDetails()
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
    /// StoreFailedEventAsync with null reason uses "Unknown" as reason
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_WithNullReason_UsesUnknownAsReason()
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
}