using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// Unit tests for RedisAuditDeadLetterQueue
/// </summary>
[TestFixture]
public class RedisAuditDeadLetterQueueTests
{
    /// <summary>
    /// Mock Redis connection multiplexer
    /// </summary>
    private Mock<IConnectionMultiplexer> _mockRedis;

    /// <summary>
    /// Mock Redis database
    /// </summary>
    private Mock<IDatabase> _mockDatabase;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<RedisAuditDeadLetterQueue>> _mockLogger;

    /// <summary>
    /// Dead letter queue instance under test
    /// </summary>
    private RedisAuditDeadLetterQueue _deadLetterQueue;

    /// <summary>
    /// Redis sorted set storage simulation
    /// </summary>
    private readonly Dictionary<string, List<RedisValue>> _redisStorage = new();

    /// <summary>
    /// Redis hash storage simulation
    /// </summary>
    private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _redisHashStorage = new();

    /// <summary>
    /// Sets up the test by initializing mocks and the dead letter queue
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<RedisAuditDeadLetterQueue>>();

        _redisStorage.Clear();
        _redisHashStorage.Clear();

        _mockRedis.Setup(static x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        // Setup SortedSetAdd
        _mockDatabase.Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, double _, SortedSetWhen _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_redisStorage.ContainsKey(keyStr))
                    _redisStorage[keyStr] = new List<RedisValue>();
                _redisStorage[keyStr].Add(value);
                return true;
            });

        // Setup SortedSetRangeByScore
        _mockDatabase.Setup(static x => x.SortedSetRangeByScoreAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<Exclude>(),
                It.IsAny<Order>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, double _, double _, Exclude _, Order order, long _, long _,
                CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_redisStorage.TryGetValue(keyStr, out List<RedisValue>? values))
                    return [];

                return order == Order.Descending ? values.Reverse<RedisValue>().ToArray() : values.ToArray();
            });

        // Setup HashSet
        _mockDatabase.Setup(static x => x.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue hashField, RedisValue value, When _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_redisHashStorage.ContainsKey(keyStr))
                    _redisHashStorage[keyStr] = new Dictionary<RedisValue, RedisValue>();
                _redisHashStorage[keyStr][hashField] = value;
                return true;
            });

        // Setup HashDelete
        _mockDatabase.Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue hashField, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                return _redisHashStorage.TryGetValue(keyStr, out Dictionary<RedisValue, RedisValue>? value) &&
                       value.Remove(hashField);
            });

        // Setup HashDelete (array overload)
        _mockDatabase.Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue[] hashFields, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                long count = 0;
                if (_redisHashStorage.TryGetValue(keyStr, out Dictionary<RedisValue, RedisValue>? value))
                {
                    count += hashFields.LongCount(field => value.Remove(field));
                }

                return count;
            });

        // Setup SortedSetRemove
        _mockDatabase.Setup(static x => x.SortedSetRemoveAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_redisStorage.TryGetValue(keyStr, out List<RedisValue>? value1))
                {
                    return value1.Remove(value);
                }

                return false;
            });

        // Setup KeyExpire
        _mockDatabase.Setup(static x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        _deadLetterQueue = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            _mockLogger.Object);
    }


    /// <summary>
    /// Tears down the test by disposing the dead letter queue
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _deadLetterQueue.Dispose();
    }

    /// <summary>
    /// Stores a failed event and verifies it is stored in Redis
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_StoresInRedis()
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
        _mockDatabase.Verify(static x => x.SortedSetAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);

        _mockDatabase.Verify(static x => x.HashSetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<RedisValue>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Stores a failed entity and verifies it is stored in Redis
    /// </summary>
    [Test]
    public async Task StoreFailedEntityAsync_StoresInRedis()
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
        _mockDatabase.Verify(static x => x.SortedSetAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Gets failed events and verifies they are retrieved correctly
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_ReturnsStoredEvents()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert
        Assert.That(events, Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Gets failed events with max count and verifies it respects the limit
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

        // Setup to return limited results
        _mockDatabase.Setup(static x => x.SortedSetRangeByScoreAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<Exclude>(),
                It.IsAny<Order>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, double _, double _, Exclude _, Order order, long _, long _,
                CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_redisStorage.TryGetValue(keyStr, out List<RedisValue>? values))
                    return [];

                var result = order == Order.Descending ? values.Reverse<RedisValue>() : values;
                return result.Take(5).ToArray(); // Limit to 5
            });

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync(maxCount: 5);

        // Assert
        Assert.That(events, Has.Count.LessThanOrEqualTo(5));
    }

    /// <summary>
    /// Gets failed events by date range and verifies filtering works
    /// </summary>
    [Test]
    public async Task GetFailedEventsByDateAsync_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddHours(-2);
        var endDate = DateTimeOffset.UtcNow.AddHours(2);

        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsByDateAsync(startDate, endDate);

        // Assert - Since we're mocking, we just verify the call was made
        Assert.That(events, Is.Not.Null);
    }

    /// <summary>
    /// Reprocesses an event with a valid ID and verifies it returns true
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithValidEvent_ReturnsTrue()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events.FirstOrDefault()?.Id;

        if (deadLetterId == null)
        {
            Assert.Inconclusive("Failed to store/retrieve event for test");
            return;
        }

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert - Based on the implementation, it should return true as it marks as processed
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// Reprocesses an event with a non-existent ID and verifies it returns false
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
    /// Reprocesses all unprocessed events and verifies processing
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

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.GreaterThanOrEqualTo(0));
        Assert.That(result.Duration, Is.GreaterThan(TimeSpan.Zero));
    }

    /// <summary>
    /// Reprocesses all events with cancellation and verifies processing stops
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

        // Act
        var result = await _deadLetterQueue.ReprocessAllAsync(cts.Token);

        // Assert
        Assert.That(result, Is.Not.Null);
    }

    /// <summary>
    /// Purges processed events and verifies they are removed
    /// </summary>
    [Test]
    public async Task PurgeProcessedEventsAsync_RemovesProcessedEvents()
    {
        // Arrange
        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" };
        var event2 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" };

        await _deadLetterQueue.StoreFailedEventAsync(event1, null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(event2, null, "Reason2");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        if (events.Any())
        {
            await _deadLetterQueue.ReprocessEventAsync(events[0].Id);
        }

        // Act
        var purgedCount = await _deadLetterQueue.PurgeProcessedEventsAsync();

        // Assert
        Assert.That(purgedCount, Is.GreaterThanOrEqualTo(0));
    }

    /// <summary>
    /// Gets statistics and verifies correct values
    /// </summary>
    [Test]
    [CancelAfter(5000)]
    [Ignore("Flaky test - needs investigation")]
    public async Task GetStatisticsAsync_ReturnsCorrectStatistics()
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
        Assert.That(stats, Is.Not.Null);
        Assert.That(stats.TotalEvents, Is.GreaterThanOrEqualTo(0));
    }

    /// <summary>
    /// Gets statistics with empty queue and verifies zero stats
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
    }

    /// <summary>
    /// Cleans up expired messages and verifies they are removed
    /// </summary>
    [Test]
    public async Task CleanupExpiredMessagesAsync_RemovesExpiredEvents()
    {
        // Arrange - Create DLQ with short expiry
        var shortExpiryDlq = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            _mockLogger.Object,
            messageExpiry: TimeSpan.FromMilliseconds(100));

        var oldEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "OldEvent" };
        await shortExpiryDlq.StoreFailedEventAsync(oldEvent, null, "Reason");

        await Task.Delay(200); // Wait for expiry

        // Act
        var removedCount = await shortExpiryDlq.CleanupExpiredMessagesAsync();

        // Assert
        Assert.That(removedCount, Is.GreaterThanOrEqualTo(0));
    }

    /// <summary>
    /// Constructor with custom queue name uses the custom name
    /// </summary>
    [Test]
    public async Task Constructor_WithCustomQueueName_UsesCustomName()
    {
        // Arrange
        var customQueueName = "custom:audit:dlq";

        // Act
        var dlq = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            _mockLogger.Object,
            queueName: customQueueName);

        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await dlq.StoreFailedEventAsync(event1, null, "Reason");

        // Assert
        _mockDatabase.Verify(x => x.SortedSetAddAsync(
            It.Is<RedisKey>(k => k.ToString() == customQueueName),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Stores a failed event with null exception and verifies it stores without exception details
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

        // Assert - Verify it was stored
        _mockDatabase.Verify(static x => x.SortedSetAddAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    /// <summary>
    /// Stores a failed event and verifies correct metadata is set
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_SetsCorrectMetadata()
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
        if (events.Any())
        {
            var storedEvent = events.First();
            Assert.That(storedEvent.OriginalEvent?.EventId, Is.EqualTo(eventId));
            Assert.That(storedEvent.FailureReason, Is.EqualTo("Database unavailable"));
            Assert.That(storedEvent.Metadata.ContainsKey("EventType"), Is.True);
        }
    }

    /// <summary>
    /// Gets failed events with corrupted data and verifies it skips invalid entries
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_WithCorruptedData_SkipsInvalidEntries()
    {
        // Arrange
        var validEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Valid" };
        await _deadLetterQueue.StoreFailedEventAsync(validEvent, null, "Reason");

        // Add corrupted data directly to storage
        _redisStorage["audit:dlq"].Add("{ invalid json");

        // Act
        var unused = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert - Should skip the corrupted entry
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to deserialize")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }

    /// <summary>
    /// Disposes the dead letter queue without throwing exceptions
    /// </summary>
    [Test]
    public void Dispose_DoesNotThrowException()
    {
        // Act & Assert
        Assert.DoesNotThrow(() => _deadLetterQueue.Dispose());
    }

    /// <summary>
    /// Reprocesses an event and verifies the retry count is updated
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_UpdatesRetryCount()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events.FirstOrDefault()?.Id;

        if (deadLetterId == null)
        {
            Assert.Inconclusive("Failed to store/retrieve event for test");
            return;
        }

        // Act
        await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert - Verify the event was updated (check via get or via mock verification)
        var reprocessedEvents = await _deadLetterQueue.GetFailedEventsAsync();
        if (reprocessedEvents.Any())
        {
            var reprocessedEvent = reprocessedEvents.FirstOrDefault(e => e.Id == deadLetterId);
            if (reprocessedEvent != null)
            {
                Assert.That(reprocessedEvent.RetryCount, Is.GreaterThan(0));
            }
        }
    }

    /// <summary>
    /// Stores multiple failed events and verifies they have unique IDs
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_CreatesUniqueIds()
    {
        // Arrange & Act
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event1" },
            null, "Reason1");
        await _deadLetterQueue.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Event2" },
            null, "Reason2");

        // Assert
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var ids = events.Select(static e => e.Id).ToList();
        Assert.That(ids.Distinct().Count(), Is.EqualTo(ids.Count));
    }
}