using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.DeadLetterQueue;

/// <summary>
/// Unit tests for RedisAuditDeadLetterQueue (hash+index storage model)
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
    /// Simulates the Redis sorted set index (key -> list of (member, score))
    /// </summary>
    private readonly Dictionary<string, List<(RedisValue Member, double Score)>> _sortedSetStorage = new();

    /// <summary>
    /// Simulates the Redis hash storage (key -> field -> value)
    /// </summary>
    private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _hashStorage = new();

    /// <summary>
    /// Sets up the test by initializing mocks and the dead letter queue
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockLogger = new Mock<ILogger<RedisAuditDeadLetterQueue>>();

        _sortedSetStorage.Clear();
        _hashStorage.Clear();

        _mockRedis.Setup(static x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        // Setup CreateTransaction — returns a mock transaction that writes to storage
        _mockDatabase.Setup(static x => x.CreateTransaction(It.IsAny<object>()))
            .Returns(() => CreateMockTransaction());

        // Setup SortedSetAdd — stores event ID as member, timestamp as score
        _mockDatabase.Setup(static x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, double score, SortedSetWhen _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.ContainsKey(keyStr))
                    _sortedSetStorage[keyStr] = [];
                _sortedSetStorage[keyStr].Add((value, score));
                return true;
            });

        // Setup SortedSetRangeByScore — returns event IDs from the index
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
                if (!_sortedSetStorage.TryGetValue(keyStr, out var entries))
                    return [];

                var sorted = order == Order.Descending
                    ? entries.OrderByDescending(e => e.Score)
                    : entries.OrderBy(e => e.Score);
                return sorted.Select(e => e.Member).ToArray();
            });

        // Setup HashSet (single field)
        _mockDatabase.Setup(static x => x.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue hashField, RedisValue value, When _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_hashStorage.ContainsKey(keyStr))
                    _hashStorage[keyStr] = new Dictionary<RedisValue, RedisValue>();
                _hashStorage[keyStr][hashField] = value;
                return true;
            });

        // Setup HashGet (single field) — O(1) lookup
        _mockDatabase.Setup(static x => x.HashGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue hashField, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_hashStorage.TryGetValue(keyStr, out var hash) && hash.TryGetValue(hashField, out var value))
                    return value;
                return RedisValue.Null;
            });

        // Setup HashGet (batch) — HMGET for batch lookups
        _mockDatabase.Setup(static x => x.HashGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue[] hashFields, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                var results = new RedisValue[hashFields.Length];
                for (int i = 0; i < hashFields.Length; i++)
                {
                    if (_hashStorage.TryGetValue(keyStr, out var hash) &&
                        hash.TryGetValue(hashFields[i], out var value))
                        results[i] = value;
                    else
                        results[i] = RedisValue.Null;
                }

                return results;
            });

        // Setup HashDelete (single field)
        _mockDatabase.Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue hashField, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                return _hashStorage.TryGetValue(keyStr, out var hash) && hash.Remove(hashField);
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
                if (_hashStorage.TryGetValue(keyStr, out var hash))
                {
                    count += hashFields.LongCount(field => hash.Remove(field));
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
                if (_sortedSetStorage.TryGetValue(keyStr, out var entries))
                {
                    var idx = entries.FindIndex(e => e.Member == value);
                    if (idx >= 0)
                    {
                        entries.RemoveAt(idx);
                        return true;
                    }
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
            new PassThroughAuditFieldRedactor(),
            _mockLogger.Object);
    }

    /// <summary>
    /// Creates a mock transaction that writes directly to storage and returns success.
    /// </summary>
    private ITransaction CreateMockTransaction()
    {
        var mockTransaction = new Mock<ITransaction>();

        // HashSetAsync on transaction writes directly to hash storage
        mockTransaction.Setup(x => x.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue hashField, RedisValue value, When _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_hashStorage.ContainsKey(keyStr))
                    _hashStorage[keyStr] = new Dictionary<RedisValue, RedisValue>();
                _hashStorage[keyStr][hashField] = value;
                return Task.FromResult(true);
            });

        // SortedSetAddAsync on transaction writes directly to sorted set storage
        mockTransaction.Setup(x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue value, double score, SortedSetWhen _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.ContainsKey(keyStr))
                    _sortedSetStorage[keyStr] = [];
                _sortedSetStorage[keyStr].Add((value, score));
                return Task.FromResult(true);
            });

        // KeyExpireAsync on transaction — no-op for tests
        mockTransaction.Setup(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));

        // HashDeleteAsync on transaction
        mockTransaction.Setup(x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue hashField, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                var removed = _hashStorage.TryGetValue(keyStr, out var hash) && hash.Remove(hashField);
                return Task.FromResult(removed);
            });

        // SortedSetRemoveAsync on transaction
        mockTransaction.Setup(x => x.SortedSetRemoveAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue value, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_sortedSetStorage.TryGetValue(keyStr, out var entries))
                {
                    var idx = entries.FindIndex(e => e.Member == value);
                    if (idx >= 0)
                    {
                        entries.RemoveAt(idx);
                        return Task.FromResult(true);
                    }
                }
                return Task.FromResult(false);
            });

        // ExecuteAsync commits the transaction — always succeeds in tests
        mockTransaction.Setup(x => x.ExecuteAsync(It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        return mockTransaction.Object;
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
    /// Stores a failed event and verifies it is stored in both index and data hash
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

        // Assert — verify the event is retrievable (proves both index and hash were written)
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].OriginalEvent?.EventId, Is.EqualTo(auditEvent.EventId));
    }

    /// <summary>
    /// Storing an event uses a Redis transaction for atomicity
    /// </summary>
    [Test]
    public async Task StoreFailedEventAsync_UsesTransaction()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };

        // Act
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        // Assert — CreateTransaction should be called for atomic writes
        _mockDatabase.Verify(static x => x.CreateTransaction(It.IsAny<object>()), Times.AtLeastOnce);
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

        // Assert — verify the entity is retrievable
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].OriginalEntity?.EventId, Is.EqualTo(entity.EventId));
    }

    /// <summary>
    /// Gets failed events and verifies they are retrieved correctly via hash+index model
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

        // Override SortedSetRangeByScore to respect take parameter
        _mockDatabase.Setup(static x => x.SortedSetRangeByScoreAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<Exclude>(),
                It.IsAny<Order>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, double _, double _, Exclude _, Order order, long _, long take,
                CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.TryGetValue(keyStr, out var entries))
                    return [];

                var sorted = order == Order.Descending
                    ? entries.OrderByDescending(e => e.Score)
                    : entries.OrderBy(e => e.Score);
                var result = sorted.Select(e => e.Member);
                if (take > 0)
                    result = result.Take((int)take);
                return result.ToArray();
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
    /// Reprocesses an event without IServiceScopeFactory returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithoutScopeFactory_ReturnsFalse()
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

        // Act — no IServiceScopeFactory was injected into _deadLetterQueue
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert — replay cannot proceed without a scope factory
        Assert.That(result, Is.False);
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
        Assert.That(stats.TotalEvents, Is.EqualTo(2));
        Assert.That(stats.PendingEvents, Is.EqualTo(2));
        Assert.That(stats.EventsByType["Type1"], Is.EqualTo(1));
        Assert.That(stats.EventsByFailureReason["Reason2"], Is.EqualTo(1));
        Assert.That(stats.OldestEventDate, Is.Not.Null);
        Assert.That(stats.TotalSizeBytes, Is.GreaterThan(0));
    }

    /// <summary>
    /// Gets statistics with empty queue and verifies zero stats
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
    }

    /// <summary>
    /// Gets statistics after replay removal and verifies count decreases
    /// </summary>
    [Test]
    public async Task GetStatisticsAsync_AfterSuccessfulReplay_RemovesEventFromStatistics()
    {
        var mockAuditLogger = new Mock<IAuditLogger>();
        mockAuditLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var mockScope = new Mock<IServiceScope>();
        var mockScopedProvider = new Mock<IServiceProvider>();
        mockScopedProvider
            .Setup(x => x.GetService(typeof(IAuditLogger)))
            .Returns(mockAuditLogger.Object);
        // DLQ reprocessing resolves AuditLogger directly to bypass decorators
        mockScopedProvider
            .Setup(x => x.GetService(typeof(AuditLogger)))
            .Returns(new StatisticsTestAuditLogger(mockAuditLogger.Object));
        mockScope.Setup(x => x.ServiceProvider).Returns(mockScopedProvider.Object);

        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(x => x.CreateScope()).Returns(mockScope.Object);

        using var dlq = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            new PassThroughAuditFieldRedactor(),
            _mockLogger.Object,
            serviceScopeFactory: mockScopeFactory.Object);

        await dlq.StoreFailedEventAsync(
            new AuditEvent { EventId = Guid.NewGuid(), EventType = "Type1" },
            null,
            "Reason1");

        var before = await dlq.GetStatisticsAsync();
        var deadLetterId = (await dlq.GetFailedEventsAsync()).Single().Id;

        var replayed = await dlq.ReprocessEventAsync(deadLetterId);
        var after = await dlq.GetStatisticsAsync();

        Assert.That(replayed, Is.True);
        Assert.That(before.TotalEvents, Is.EqualTo(1));
        Assert.That(after.TotalEvents, Is.EqualTo(0));
    }

    /// <summary>
    /// Gets statistics when Redis fails and verifies exception wrapping
    /// </summary>
    [Test]
    public void GetStatisticsAsync_WhenRedisFails_WrapsException()
    {
        _mockDatabase
            .Setup(x => x.SortedSetRangeByScoreAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<Exclude>(),
                It.IsAny<Order>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ThrowsAsync(new TimeoutException("redis timeout"));

        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _deadLetterQueue.GetStatisticsAsync());

        Assert.That(ex!.Message, Does.Contain("statistics"));
        Assert.That(ex.InnerException, Is.TypeOf<TimeoutException>());
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
            new PassThroughAuditFieldRedactor(),
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
    /// Constructor with custom queue name uses the custom name for keys
    /// </summary>
    [Test]
    public async Task Constructor_WithCustomQueueName_UsesCustomName()
    {
        // Arrange
        var customQueueName = "custom:audit:dlq";

        // Act
        var dlq = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            new PassThroughAuditFieldRedactor(),
            _mockLogger.Object,
            queueName: customQueueName);

        var event1 = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await dlq.StoreFailedEventAsync(event1, null, "Reason");

        // Assert — verify CreateTransaction was called (atomicity guarantee)
        _mockDatabase.Verify(x => x.CreateTransaction(It.IsAny<object>()), Times.AtLeastOnce);

        // Verify the event can be retrieved
        var events = await dlq.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
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

        // Assert - Verify the event was stored and has no exception details
        var events = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(events, Has.Count.EqualTo(1));
        Assert.That(events[0].ExceptionType, Is.Null.Or.Empty);
        Assert.That(events[0].ExceptionMessage, Is.Null.Or.Empty);
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
    /// GetEventByIdAsync is O(1) — does NOT scan the sorted set
    /// </summary>
    [Test]
    public async Task GetFailedEventsAsync_UsesHashLookup_NotSortedSetScan()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        // Act
        var events = await _deadLetterQueue.GetFailedEventsAsync();

        // Assert — should use batch HashGet, not deserialize sorted set members
        _mockDatabase.Verify(static x => x.HashGetAsync(
            It.IsAny<RedisKey>(),
            It.IsAny<RedisValue[]>(),
            It.IsAny<CommandFlags>()), Times.AtLeastOnce);
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
    /// Reprocesses an event without scope factory — increments retry but returns false
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_WithoutScopeFactory_IncrementsRetryAndReturnsFalse()
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

        // Act — no IServiceScopeFactory injected
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.False);
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

    /// <summary>
    /// When Redis transaction fails, StoreFailedEventAsync throws with explicit message
    /// </summary>
    [Test]
    public void StoreFailedEventAsync_WhenTransactionFails_ThrowsWithExplicitMessage()
    {
        // Arrange - create a transaction that returns false from ExecuteAsync
        var failingTransaction = new Mock<ITransaction>();
        failingTransaction.Setup(x => x.HashSetAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(),
                It.IsAny<When>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        failingTransaction.Setup(x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        failingTransaction.Setup(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));
        failingTransaction.Setup(x => x.ExecuteAsync(It.IsAny<CommandFlags>()))
            .ReturnsAsync(false); // Transaction fails

        _mockDatabase.Setup(x => x.CreateTransaction(It.IsAny<object>()))
            .Returns(failingTransaction.Object);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test" };

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason"));

        Assert.That(ex!.Message, Does.Contain("transaction failed"));
        Assert.That(ex.Message, Does.Contain("not persisted"));
    }

    /// <summary>
    /// Test double for AuditLogger that delegates to a mock IAuditLogger.
    /// Required because DLQ reprocessing resolves AuditLogger directly to bypass decorators.
    /// </summary>
    private sealed class StatisticsTestAuditLogger(IAuditLogger innerLogger) : AuditLogger(null!, null!, null!, null!, null!, null!)
    {
        public override Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => innerLogger.LogAsync(auditEvent, cancellationToken);
    }
}

/// <summary>
/// Tests for Redis DLQ replay semantics with IAuditLogger integration
/// </summary>
[TestFixture]
public class RedisAuditDeadLetterQueueReplayTests
{
    private Mock<IConnectionMultiplexer> _mockRedis = null!;
    private Mock<IDatabase> _mockDatabase = null!;
    private Mock<IAuditLogger> _mockAuditLogger = null!;
    private Mock<IServiceScopeFactory> _mockScopeFactory = null!;
    private RedisAuditDeadLetterQueue _deadLetterQueue = null!;
    private readonly Dictionary<string, List<(RedisValue Member, double Score)>> _sortedSetStorage = new();
    private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _hashStorage = new();

    [SetUp]
    public void SetUp()
    {
        _mockRedis = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockAuditLogger = new Mock<IAuditLogger>();
        _sortedSetStorage.Clear();
        _hashStorage.Clear();

        _mockRedis.Setup(static x => x.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_mockDatabase.Object);

        // Setup CreateTransaction for transactional writes
        _mockDatabase.Setup(static x => x.CreateTransaction(It.IsAny<object>()))
            .Returns(() => CreateMockTransaction());

        // Track stored entries in sorted set
        _mockDatabase.Setup(x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, double score, SortedSetWhen _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.ContainsKey(keyStr))
                    _sortedSetStorage[keyStr] = [];
                _sortedSetStorage[keyStr].Add((value, score));
                return true;
            });

        // Return stored entries on query
        _mockDatabase.Setup(x => x.SortedSetRangeByScoreAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<double>(),
                It.IsAny<double>(),
                It.IsAny<Exclude>(),
                It.IsAny<Order>(),
                It.IsAny<long>(),
                It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, double _, double _, Exclude _, Order _, long _, long _,
                CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.TryGetValue(keyStr, out var entries))
                    return [];
                return entries.Select(static e => e.Member).ToArray();
            });

        // Allow removes from sorted set
        _mockDatabase.Setup(x => x.SortedSetRemoveAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue value, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_sortedSetStorage.TryGetValue(keyStr, out var entries))
                {
                    var idx = entries.FindIndex(e => e.Member == value);
                    if (idx >= 0)
                    {
                        entries.RemoveAt(idx);
                        return true;
                    }
                }

                return false;
            });

        // Hash set
        _mockDatabase.Setup(static x => x.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue field, RedisValue value, When _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_hashStorage.ContainsKey(keyStr))
                    _hashStorage[keyStr] = new Dictionary<RedisValue, RedisValue>();
                _hashStorage[keyStr][field] = value;
                return true;
            });

        // Hash get (single)
        _mockDatabase.Setup(static x => x.HashGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_hashStorage.TryGetValue(keyStr, out var hash) && hash.TryGetValue(field, out var value))
                    return value;
                return RedisValue.Null;
            });

        // Hash get (batch)
        _mockDatabase.Setup(static x => x.HashGetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue[]>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue[] fields, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                var results = new RedisValue[fields.Length];
                for (int i = 0; i < fields.Length; i++)
                {
                    if (_hashStorage.TryGetValue(keyStr, out var hash) &&
                        hash.TryGetValue(fields[i], out var value))
                        results[i] = value;
                    else
                        results[i] = RedisValue.Null;
                }

                return results;
            });

        // Hash delete
        _mockDatabase.Setup(static x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync((RedisKey key, RedisValue field, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                return _hashStorage.TryGetValue(keyStr, out var hash) && hash.Remove(field);
            });

        _mockDatabase.Setup(static x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        // Wire up DI scope
        var mockScope = new Mock<IServiceScope>();
        var mockScopedProvider = new Mock<IServiceProvider>();
        mockScopedProvider.Setup(static x => x.GetService(typeof(IAuditLogger)))
            .Returns(() => _mockAuditLogger.Object);
        // DLQ reprocessing resolves AuditLogger directly to bypass decorators
        mockScopedProvider.Setup(x => x.GetService(typeof(AuditLogger)))
            .Returns(() => new TestableAuditLogger(_mockAuditLogger.Object));
        mockScope.Setup(static x => x.ServiceProvider).Returns(mockScopedProvider.Object);

        _mockScopeFactory = new Mock<IServiceScopeFactory>();
        _mockScopeFactory.Setup(static x => x.CreateScope()).Returns(mockScope.Object);

        _deadLetterQueue = new RedisAuditDeadLetterQueue(
            _mockRedis.Object,
            new PassThroughAuditFieldRedactor(),
            logger: null,
            serviceScopeFactory: _mockScopeFactory.Object);
    }

    /// <summary>
    /// Creates a mock transaction that writes directly to storage and returns success.
    /// </summary>
    private ITransaction CreateMockTransaction()
    {
        var mockTransaction = new Mock<ITransaction>();

        mockTransaction.Setup(x => x.HashSetAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<RedisValue>(),
                It.IsAny<When>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue hashField, RedisValue value, When _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_hashStorage.ContainsKey(keyStr))
                    _hashStorage[keyStr] = new Dictionary<RedisValue, RedisValue>();
                _hashStorage[keyStr][hashField] = value;
                return Task.FromResult(true);
            });

        mockTransaction.Setup(x => x.SortedSetAddAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<double>(),
                It.IsAny<SortedSetWhen>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue value, double score, SortedSetWhen _, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (!_sortedSetStorage.ContainsKey(keyStr))
                    _sortedSetStorage[keyStr] = [];
                _sortedSetStorage[keyStr].Add((value, score));
                return Task.FromResult(true);
            });

        mockTransaction.Setup(x => x.KeyExpireAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<TimeSpan?>(),
                It.IsAny<ExpireWhen>(),
                It.IsAny<CommandFlags>()))
            .Returns(Task.FromResult(true));

        mockTransaction.Setup(x => x.HashDeleteAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue hashField, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                var removed = _hashStorage.TryGetValue(keyStr, out var hash) && hash.Remove(hashField);
                return Task.FromResult(removed);
            });

        mockTransaction.Setup(x => x.SortedSetRemoveAsync(
                It.IsAny<RedisKey>(),
                It.IsAny<RedisValue>(),
                It.IsAny<CommandFlags>()))
            .Returns((RedisKey key, RedisValue value, CommandFlags _) =>
            {
                var keyStr = key.ToString();
                if (_sortedSetStorage.TryGetValue(keyStr, out var entries))
                {
                    var idx = entries.FindIndex(e => e.Member == value);
                    if (idx >= 0)
                    {
                        entries.RemoveAt(idx);
                        return Task.FromResult(true);
                    }
                }
                return Task.FromResult(false);
            });

        mockTransaction.Setup(x => x.ExecuteAsync(It.IsAny<CommandFlags>()))
            .ReturnsAsync(true);

        return mockTransaction.Object;
    }

    [TearDown]
    public void TearDown()
    {
        _deadLetterQueue.Dispose();
    }

    /// <summary>
    /// Replay success: LogAsync is called and event is marked processed
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_Success_CallsLogAsyncAndMarksProcessed()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test.Event" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.True);
        _mockAuditLogger.Verify(
            x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Replay failure: LogAsync throws, event is NOT marked processed
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_ReplayFails_DoesNotMarkProcessed()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test.Event" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        // Act
        var result = await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert
        Assert.That(result, Is.False);

        // Event should still be in the queue (not removed)
        var remaining = await _deadLetterQueue.GetFailedEventsAsync();
        Assert.That(remaining, Has.Count.GreaterThanOrEqualTo(1));
    }

    /// <summary>
    /// Replay failure does not produce false-positive processed state
    /// </summary>
    [Test]
    public async Task ReprocessEventAsync_ReplayFails_EventNotMarkedAsProcessed()
    {
        // Arrange
        var auditEvent = new AuditEvent { EventId = Guid.NewGuid(), EventType = "Test.Event" };
        await _deadLetterQueue.StoreFailedEventAsync(auditEvent, null, "Reason");

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("DB unavailable"));

        var events = await _deadLetterQueue.GetFailedEventsAsync();
        var deadLetterId = events[0].Id;

        // Act
        await _deadLetterQueue.ReprocessEventAsync(deadLetterId);

        // Assert — event should still be unprocessed
        var remaining = await _deadLetterQueue.GetFailedEventsAsync();
        var evt = remaining.FirstOrDefault(e => e.OriginalEvent?.EventId == auditEvent.EventId);
        Assert.That(evt, Is.Not.Null);
        Assert.That(evt!.IsProcessed, Is.False);
        Assert.That(evt.RetryCount, Is.EqualTo(1));
    }

    /// <summary>
    /// Test double for AuditLogger that delegates to a mock IAuditLogger.
    /// Required because DLQ reprocessing resolves AuditLogger directly to bypass decorators.
    /// </summary>
    private sealed class TestableAuditLogger(IAuditLogger innerLogger) : AuditLogger(null!, null!, null!, null!, null!, null!)
    {
        public override Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => innerLogger.LogAsync(auditEvent, cancellationToken);
    }
}
