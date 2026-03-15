using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for AuditEventRepository query methods.
/// </summary>
[TestFixture]
public class AuditEventRepositoryTests
{
    private DbContextOptions<AuditApplicationDbContext> _options;
    private AuditApplicationDbContext _context;
    private AuditEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(_options);
        _repository = new AuditEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region GetByEventTypeAsync

    /// <summary>
    /// Verifies filtering by event type returns only matching events.
    /// </summary>
    [Test]
    public async Task GetByEventTypeAsync_ReturnsOnlyMatchingEvents()
    {
        // Arrange
        await SeedEvents(
            ("User.Login", "user1@test.com"),
            ("User.Login", "user2@test.com"),
            ("User.Logout", "user1@test.com"));

        // Act
        var results = (await _repository.GetByEventTypeAsync("User.Login")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.EventType == "User.Login"), Is.True);
    }

    /// <summary>
    /// Verifies empty result for non-existent event type.
    /// </summary>
    [Test]
    public async Task GetByEventTypeAsync_NoMatches_ReturnsEmpty()
    {
        // Arrange
        await SeedEvents(("User.Login", "user@test.com"));

        // Act
        var results = (await _repository.GetByEventTypeAsync("NoSuchType")).ToList();

        // Assert
        Assert.That(results, Is.Empty);
    }

    #endregion

    #region GetByUserIdAsync

    /// <summary>
    /// Verifies filtering by UserId.
    /// </summary>
    [Test]
    public async Task GetByUserIdAsync_ReturnsMatchingEvents()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), UserId = userId, EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), UserId = userId, EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), UserId = otherUserId, EventType = "C", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByUserIdAsync(userId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(e => e.UserId == userId), Is.True);
    }

    #endregion

    #region GetByAspNetUserIdAsync

    /// <summary>
    /// Verifies filtering by AspNetUserId.
    /// </summary>
    [Test]
    public async Task GetByAspNetUserIdAsync_ReturnsMatchingEvents()
    {
        // Arrange
        const string aspNetUserId = "aspnet-user-123";
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), AspNetUserId = aspNetUserId, EventType = "X", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), AspNetUserId = "other", EventType = "Y", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByAspNetUserIdAsync(aspNetUserId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].AspNetUserId, Is.EqualTo(aspNetUserId));
    }

    #endregion

    #region GetByEntityAsync

    /// <summary>
    /// Verifies filtering by entity type and entity ID.
    /// </summary>
    [Test]
    public async Task GetByEntityAsync_ReturnsMatchingEvents()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = "123", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = "456", EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = "123", EventType = "C", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByEntityAsync("Order", "123")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
    }

    #endregion

    #region GetByDateRangeAsync

    /// <summary>
    /// Verifies date range filtering is inclusive.
    /// </summary>
    [Test]
    public async Task GetByDateRangeAsync_ReturnsEventsInRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = now.AddDays(-5) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = now.AddDays(-2) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "C", InsertedDate = now.AddDays(1) });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByDateRangeAsync(now.AddDays(-3), now)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].EventType, Is.EqualTo("B"));
    }

    #endregion

    #region GetByCorrelationIdAsync

    /// <summary>
    /// Verifies filtering by correlation ID.
    /// </summary>
    [Test]
    public async Task GetByCorrelationIdAsync_ReturnsMatchingEvents()
    {
        // Arrange
        var correlationId = Guid.NewGuid().ToString();
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), CorrelationId = correlationId, EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), CorrelationId = "other", EventType = "B", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByCorrelationIdAsync(correlationId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetByTenantIdAsync

    /// <summary>
    /// Verifies filtering by tenant ID.
    /// </summary>
    [Test]
    public async Task GetByTenantIdAsync_ReturnsMatchingEvents()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), TenantId = tenantId, EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), TenantId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByTenantIdAsync(tenantId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetByActionAsync

    /// <summary>
    /// Verifies filtering by action.
    /// </summary>
    [Test]
    public async Task GetByActionAsync_ReturnsMatchingEvents()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), Action = "Create", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), Action = "Delete", EventType = "B", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByActionAsync("Create")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Action, Is.EqualTo("Create"));
    }

    #endregion

    #region GetByMinimumDurationAsync

    /// <summary>
    /// Verifies filtering by minimum duration.
    /// </summary>
    [Test]
    public async Task GetByMinimumDurationAsync_ReturnsSlowEvents()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), Duration = 100, EventType = "Fast", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), Duration = 5000, EventType = "Slow", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), Duration = null, EventType = "NoValue", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByMinimumDurationAsync(1000)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].EventType, Is.EqualTo("Slow"));
    }

    #endregion

    #region GetByEnvironmentAsync

    /// <summary>
    /// Verifies filtering by environment.
    /// </summary>
    [Test]
    public async Task GetByEnvironmentAsync_ReturnsMatchingEvents()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), Environment = "Production", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), Environment = "Staging", EventType = "B", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetByEnvironmentAsync("Production")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
    }

    #endregion

    #region EventExistsAsync

    /// <summary>
    /// Verifies EventExistsAsync returns true for existing event.
    /// </summary>
    [Test]
    public async Task EventExistsAsync_WithExistingEvent_ReturnsTrue()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        await _context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId, EventType = "Test", InsertedDate = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var exists = await _repository.EventExistsAsync(eventId);

        // Assert
        Assert.That(exists, Is.True);
    }

    /// <summary>
    /// Verifies EventExistsAsync returns false for non-existent event.
    /// </summary>
    [Test]
    public async Task EventExistsAsync_WithNonExistentEvent_ReturnsFalse()
    {
        // Act
        var exists = await _repository.EventExistsAsync(Guid.NewGuid());

        // Assert
        Assert.That(exists, Is.False);
    }

    #endregion

    #region GetDistinctEventTypesAsync

    /// <summary>
    /// Verifies distinct event types are returned sorted alphabetically.
    /// </summary>
    [Test]
    public async Task GetDistinctEventTypesAsync_ReturnsDistinctSortedTypes()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B.Type", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A.Type", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B.Type", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = null, InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var types = await _repository.GetDistinctEventTypesAsync();

        // Assert
        Assert.That(types, Has.Count.EqualTo(2));
        Assert.That(types[0], Is.EqualTo("A.Type"));
        Assert.That(types[1], Is.EqualTo("B.Type"));
    }

    #endregion

    #region GetDistinctUsersAsync

    /// <summary>
    /// Verifies distinct users are returned, excluding nulls and empty strings.
    /// </summary>
    [Test]
    public async Task GetDistinctUsersAsync_ReturnsDistinctNonEmptyUsers()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "alice", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "bob", EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "alice", EventType = "C", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = null, EventType = "D", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "", EventType = "E", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var users = await _repository.GetDistinctUsersAsync();

        // Assert
        Assert.That(users, Has.Count.EqualTo(2));
        Assert.That(users, Does.Contain("alice"));
        Assert.That(users, Does.Contain("bob"));
    }

    #endregion

    #region GetUniqueUserCountAsync

    /// <summary>
    /// Verifies unique user count without predicate.
    /// </summary>
    [Test]
    public async Task GetUniqueUserCountAsync_NoPredicate_ReturnsDistinctCount()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "alice", EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "bob", EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), User = "alice", EventType = "C", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var count = await _repository.GetUniqueUserCountAsync();

        // Assert
        Assert.That(count, Is.EqualTo(2));
    }

    #endregion

    // GroupBy projection tests moved to Integration/AuditEventIntegrationTests.cs

    #region GetForTamperDetectionAsync

    /// <summary>
    /// Verifies events are returned in ascending order by InsertedDate.
    /// </summary>
    [Test]
    public async Task GetForTamperDetectionAsync_ReturnsAscendingOrder()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "C", InsertedDate = now.AddMinutes(-1) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = now.AddMinutes(-3) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = now.AddMinutes(-2) });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetForTamperDetectionAsync(now.AddMinutes(-5))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results[0].EventType, Is.EqualTo("A"));
        Assert.That(results[1].EventType, Is.EqualTo("B"));
        Assert.That(results[2].EventType, Is.EqualTo("C"));
    }

    /// <summary>
    /// Verifies maxResults is respected.
    /// </summary>
    [Test]
    public async Task GetForTamperDetectionAsync_RespectsMaxResults()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event{i}",
                InsertedDate = now.AddMinutes(-i)
            });
        }
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetForTamperDetectionAsync(now.AddMinutes(-20), maxResults: 3)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Verifies only events from the given date onward are returned.
    /// </summary>
    [Test]
    public async Task GetForTamperDetectionAsync_FiltersFromDate()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Old", InsertedDate = now.AddDays(-10) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Recent", InsertedDate = now.AddDays(-1) });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetForTamperDetectionAsync(now.AddDays(-5))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].EventType, Is.EqualTo("Recent"));
    }

    #endregion

    #region GetWithIntegrityAsync

    /// <summary>
    /// Verifies events are returned with their integrity records included.
    /// </summary>
    [Test]
    public async Task GetWithIntegrityAsync_IncludesIntegrityRecords()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Integrity.Test",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.AuditIntegrity.AddAsync(new AuditIntegrityEntity
        {
            EventId = eventId,
            SequenceNumber = 1,
            EventHash = "hash1",
            TrustedTimestamp = DateTimeOffset.UtcNow,
            Checksum = "checksum"
        });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetWithIntegrityAsync([eventId])).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].AuditIntegrity, Is.Not.Null);
        Assert.That(results[0].AuditIntegrity!.EventHash, Is.EqualTo("hash1"));
    }

    /// <summary>
    /// Verifies only requested event IDs are returned.
    /// </summary>
    [Test]
    public async Task GetWithIntegrityAsync_ReturnsOnlyRequestedEvents()
    {
        // Arrange
        var targetId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = targetId, EventType = "Target", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = otherId, EventType = "Other", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetWithIntegrityAsync([targetId])).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].EventId, Is.EqualTo(targetId));
    }

    #endregion

    #region Helpers

    private async Task SeedEvents(params (string EventType, string User)[] events)
    {
        foreach (var (eventType, user) in events)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = eventType,
                User = user,
                InsertedDate = DateTimeOffset.UtcNow
            });
        }
        await _context.SaveChangesAsync();
    }

    #endregion
}
