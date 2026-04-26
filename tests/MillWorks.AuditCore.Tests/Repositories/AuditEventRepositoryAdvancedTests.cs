using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for AuditEventRepository-specific query and aggregation methods.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditEventRepositoryAdvancedTests
{
    private AuditDbContext _context;
    private AuditEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();
        _context = new AuditDbContext(options);
        _repository = new AuditEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region GetByCorrelationIdAsync

    /// <summary>
    /// Verifies that GetByCorrelationIdAsync returns only events matching the given correlation ID.
    /// </summary>
    [Test]
    public async Task GetByCorrelationIdAsync_ReturnsCorrelatedEvents()
    {
        // Arrange
        const string correlationId = "corr-123";

        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), CorrelationId = correlationId, EventType = "A",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), CorrelationId = correlationId, EventType = "B",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), CorrelationId = correlationId, EventType = "C",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), CorrelationId = "other-corr-1", EventType = "D",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), CorrelationId = "other-corr-2", EventType = "E",
                InsertedDate = DateTimeOffset.UtcNow
            });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByCorrelationIdAsync(correlationId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results.All(static e => e.CorrelationId == correlationId), Is.True);
    }

    #endregion

    #region GetByTenantIdAsync

    /// <summary>
    /// Verifies that GetByTenantIdAsync returns only events scoped to the specified tenant.
    /// </summary>
    [Test]
    public async Task GetByTenantIdAsync_ReturnsTenantScopedEvents()
    {
        // Arrange
        var tenantGuid = Guid.NewGuid();
        var otherTenant = Guid.NewGuid();

        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), TenantId = tenantGuid, EventType = "A", InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), TenantId = tenantGuid, EventType = "B", InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), TenantId = otherTenant, EventType = "C", InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), TenantId = null, EventType = "D", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByTenantIdAsync(tenantGuid)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(e => e.TenantId == tenantGuid), Is.True);
    }

    #endregion

    #region GetByActionAsync

    /// <summary>
    /// Verifies that GetByActionAsync returns only events matching the specified action.
    /// </summary>
    [Test]
    public async Task GetByActionAsync_FiltersCorrectly()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Action = "Created", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Action = "Created", EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Action = "Updated", EventType = "C", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Action = "Updated", EventType = "D", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Action = "Deleted", EventType = "E", InsertedDate = DateTimeOffset.UtcNow
            });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByActionAsync("Created")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.Action == "Created"), Is.True);
    }

    #endregion

    #region GetByMinimumDurationAsync

    /// <summary>
    /// Verifies that GetByMinimumDurationAsync returns only events with Duration >= the threshold,
    /// excluding null-Duration events.
    /// </summary>
    [Test]
    public async Task GetByMinimumDurationAsync_ReturnsSlowEvents()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Duration = 50, EventType = "VeryFast", InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Duration = 100, EventType = "Fast", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), Duration = 200, EventType = "Slow", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Duration = 500, EventType = "VerySlow", InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Duration = null, EventType = "NoDuration",
                InsertedDate = DateTimeOffset.UtcNow
            });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByMinimumDurationAsync(150)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.Duration is >= 150), Is.True);
        Assert.That(results.Any(static e => e.EventType == "Slow"), Is.True);
        Assert.That(results.Any(static e => e.EventType == "VerySlow"), Is.True);
    }

    #endregion

    #region GetByEnvironmentAsync

    /// <summary>
    /// Verifies that GetByEnvironmentAsync returns only events matching the specified environment.
    /// </summary>
    [Test]
    public async Task GetByEnvironmentAsync_FiltersCorrectly()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Environment = "Production", EventType = "A",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Environment = "Production", EventType = "B",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Environment = "Development", EventType = "C",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new AuditEventEntity
            {
                EventId = Guid.NewGuid(), Environment = "Development", EventType = "D",
                InsertedDate = DateTimeOffset.UtcNow
            });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByEnvironmentAsync("Production")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.Environment == "Production"), Is.True);
    }

    #endregion

    #region GetWithIntegrityAsync

    /// <summary>
    /// Verifies that GetWithIntegrityAsync eagerly loads the AuditIntegrity navigation property
    /// for events that have a matching integrity record.
    /// </summary>
    [Test]
    public async Task GetWithIntegrityAsync_JoinsIntegrityRecords()
    {
        // Arrange
        var eventId = Guid.NewGuid();

        await _context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Tamper.Check",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await _context.AuditIntegrity.AddAsync(new AuditIntegrityEntity
        {
            EventId = eventId,
            EventHash = "abc123hash456def789abc123hash456def789abc123hash456def789abc1234",
            Checksum = "checksum+base64+value+here+44chars==",
            TrustedTimestamp = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetWithIntegrityAsync(new[] { eventId })).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].EventId, Is.EqualTo(eventId));
        Assert.That(results[0].AuditIntegrity, Is.Not.Null);
        Assert.That(results[0].AuditIntegrity!.EventId, Is.EqualTo(eventId));
    }

    #endregion

    #region GetForTamperDetectionAsync

    /// <summary>
    /// Verifies that GetForTamperDetectionAsync returns events after the specified date,
    /// ordered by InsertedDate ascending.
    /// </summary>
    [Test]
    public async Task GetForTamperDetectionAsync_ReturnsRequiredFields()
    {
        // Arrange
        var fromDate = DateTimeOffset.UtcNow.AddDays(-3);

        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "Old", InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "First", InsertedDate = DateTimeOffset.UtcNow.AddDays(-2) },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "Second", InsertedDate = DateTimeOffset.UtcNow.AddDays(-1) },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "Third", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetForTamperDetectionAsync(fromDate)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        // Verify ascending order by InsertedDate
        Assert.That(results[0].EventType, Is.EqualTo("First"));
        Assert.That(results[1].EventType, Is.EqualTo("Second"));
        Assert.That(results[2].EventType, Is.EqualTo("Third"));
    }

    #endregion

    #region GetUniqueUserCountAsync

    /// <summary>
    /// Verifies that GetUniqueUserCountAsync returns the count of distinct non-null, non-empty User strings.
    /// </summary>
    [Test]
    public async Task GetUniqueUserCountAsync_ReturnsDistinctCount()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user1", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user1", EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user2", EventType = "C", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user3", EventType = "D", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = null, EventType = "E", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "", EventType = "F", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var count = await _repository.GetUniqueUserCountAsync();

        // Assert
        Assert.That(count, Is.EqualTo(3));
    }

    #endregion

    #region GetEventTypeCountsAsync

    /// <summary>
    /// Verifies that GetEventTypeCountsAsync returns a list of KeyValuePairs with event type names
    /// and their correct counts, grouped server-side.
    /// </summary>
    [Test]
    public async Task GetEventTypeCountsAsync_ReturnsGroupedCounts()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "Order.Created", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "Order.Created", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity
                { EventId = Guid.NewGuid(), EventType = "User.Logout", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var counts = await _repository.GetEventTypeCountsAsync();

        // Assert
        Assert.That(counts, Is.Not.Empty);

        var loginCount = counts.FirstOrDefault(static kvp => kvp.Key == "User.Login");
        var orderCount = counts.FirstOrDefault(static kvp => kvp.Key == "Order.Created");
        var logoutCount = counts.FirstOrDefault(static kvp => kvp.Key == "User.Logout");

        Assert.That(loginCount.Value, Is.EqualTo(3));
        Assert.That(orderCount.Value, Is.EqualTo(2));
        Assert.That(logoutCount.Value, Is.EqualTo(1));

        // Ordered descending by count: User.Login first
        Assert.That(counts[0].Key, Is.EqualTo("User.Login"));
    }

    #endregion

    #region GetTopUsersByActivityAsync

    /// <summary>
    /// Verifies that GetTopUsersByActivityAsync returns the top N users ordered by activity count descending,
    /// grouped by User string.
    /// </summary>
    [Test]
    public async Task GetTopUsersByActivityAsync_OrdersDescending()
    {
        // Arrange — user3 has 8 events, user1 has 5, user2 has 2
        var events = new List<AuditEventEntity>();

        for (var i = 0; i < 5; i++)
            events.Add(new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user1", EventType = "A", InsertedDate = DateTimeOffset.UtcNow });

        for (var i = 0; i < 2; i++)
            events.Add(new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user2", EventType = "B", InsertedDate = DateTimeOffset.UtcNow });

        for (var i = 0; i < 8; i++)
            events.Add(new AuditEventEntity
                { EventId = Guid.NewGuid(), User = "user3", EventType = "C", InsertedDate = DateTimeOffset.UtcNow });

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var top2 = await _repository.GetTopUsersByActivityAsync(take: 2);

        // Assert
        Assert.That(top2, Has.Count.EqualTo(2));
        // First entry should be user3 (8 events)
        Assert.That(top2[0].Key, Is.EqualTo("user3"));
        Assert.That(top2[0].Value, Is.EqualTo(8));
        // Second entry should be user1 (5 events)
        Assert.That(top2[1].Key, Is.EqualTo("user1"));
        Assert.That(top2[1].Value, Is.EqualTo(5));
    }

    #endregion

    #region GetDailyEventCountsAsync

    /// <summary>
    /// Verifies that GetDailyEventCountsAsync groups events by date within the specified range
    /// and returns the correct counts per date.
    /// </summary>
    [Test]
    public async Task GetDailyEventCountsAsync_GroupsByDate()
    {
        // Arrange
        var day1 = new DateTimeOffset(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
        var day2 = new DateTimeOffset(2026, 1, 11, 12, 0, 0, TimeSpan.Zero);
        var day3 = new DateTimeOffset(2026, 1, 12, 12, 0, 0, TimeSpan.Zero);
        var beforeRange = new DateTimeOffset(2026, 1, 9, 12, 0, 0, TimeSpan.Zero);

        await _context.AuditEvents.AddRangeAsync(
            // day1: 2 events
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day1 },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day1.AddHours(2) },
            // day2: 3 events
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day2 },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day2.AddHours(1) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day2.AddHours(3) },
            // day3: 1 event
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = day3 },
            // outside range: should not appear
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "X", InsertedDate = beforeRange });
        await _context.SaveChangesAsync();

        var startDate = new DateTimeOffset(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);
        var endDate = new DateTimeOffset(2026, 1, 12, 23, 59, 59, TimeSpan.Zero);

        // Act
        var results = await _repository.GetDailyEventCountsAsync(startDate, endDate);

        // Assert
        Assert.That(results, Is.Not.Empty);

        var totalCount = results.Sum(static r => r.Count);
        Assert.That(totalCount, Is.EqualTo(6));

        var day1Results = results.Where(r => r.Date.Date == day1.Date).ToList();
        var day2Results = results.Where(r => r.Date.Date == day2.Date).ToList();
        var day3Results = results.Where(r => r.Date.Date == day3.Date).ToList();

        Assert.That(day1Results.Sum(static r => r.Count), Is.EqualTo(2));
        Assert.That(day2Results.Sum(static r => r.Count), Is.EqualTo(3));
        Assert.That(day3Results.Sum(static r => r.Count), Is.EqualTo(1));
    }

    #endregion
}
