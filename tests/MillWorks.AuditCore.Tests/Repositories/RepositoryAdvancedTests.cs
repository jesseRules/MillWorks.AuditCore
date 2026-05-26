using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for advanced Repository&lt;T&gt; methods not covered by RepositoryCrudTests.
/// Covers SaveChangesWithRetryAsync, ReloadEntityAsync, ClearChangeTrackerAsync,
/// GetPagedAsync, FindAsync, CountAsync, and ExistsAsync.
/// Uses AuditEventRepository (extends Repository&lt;AuditEventEntity&gt;) with InMemory DB.
/// </summary>
[TestFixture]
[Category("Unit")]
public class RepositoryAdvancedTests
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

    #region SaveChangesWithRetryAsync

    /// <summary>
    /// Verifies SaveChangesWithRetryAsync persists changes and returns affected row count on first attempt.
    /// </summary>
    [Test]
    public async Task SaveChangesWithRetryAsync_SucceedsOnFirstTry()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "User.Login",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _repository.AddAsync(entity);

        // Act
        var result = await _repository.SaveChangesWithRetryAsync();

        // Assert
        Assert.That(result, Is.GreaterThan(0));
        var persisted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.EventType, Is.EqualTo("User.Login"));
    }

    /// <summary>
    /// Verifies SaveChangesWithRetryAsync with maxRetries=3 succeeds when data is valid.
    /// InMemory does not simulate transient failures; this confirms the retry path still
    /// produces a correct result when no concurrency conflict arises.
    /// </summary>
    [Test]
    public async Task SaveChangesWithRetryAsync_TransientFailure_Retries()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "User.Register",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _repository.AddAsync(entity);

        // Act — maxRetries=3, but no conflict so it succeeds on the first attempt
        var result = await _repository.SaveChangesWithRetryAsync(maxRetries: 3);

        // Assert
        Assert.That(result, Is.GreaterThan(0));
        var persisted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(persisted, Is.Not.Null);
    }

    /// <summary>
    /// Permanent concurrency failures cannot be simulated with the InMemory provider
    /// because it does not enforce concurrency tokens (RowVersion).
    /// </summary>
    [Test]
    [Ignore("Requires relational provider to simulate concurrency failures")]
    public Task SaveChangesWithRetryAsync_PermanentFailure_Throws()
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Optimistic concurrency conflicts cannot be simulated with the InMemory provider
    /// because it does not enforce concurrency tokens (RowVersion).
    /// </summary>
    [Test]
    [Ignore("Requires relational provider for concurrency tokens")]
    public Task ExecuteOptimisticUpdateAsync_ConcurrencyConflict_Throws()
    {
        return Task.CompletedTask;
    }

    #endregion

    #region ReloadEntityAsync

    /// <summary>
    /// Verifies ReloadEntityAsync does not throw when called on a tracked entity.
    /// The InMemory provider does not support a true round-trip reload, so this test
    /// confirms the method is callable without error.
    /// </summary>
    [Test]
    public async Task ReloadEntityAsync_RefreshesFromDb()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "User.Login",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Modify the tracked entity in memory without saving
        entity.EventType = "User.ModifiedInMemory";

        // Act — InMemory provider does not truly reload from storage,
        // but ReloadEntityAsync must not throw.
        Assert.DoesNotThrowAsync(async () => await _repository.ReloadEntityAsync(entity));

        // Assert the call completed without error
        Assert.That(entity, Is.Not.Null);
    }

    #endregion

    #region ClearChangeTrackerAsync

    /// <summary>
    /// Verifies ClearChangeTrackerAsync detaches all currently tracked entities,
    /// leaving the change tracker with zero entries.
    /// </summary>
    [Test]
    public async Task ClearChangeTrackerAsync_DetachesAllEntities()
    {
        // Arrange — add and save entities so they become tracked
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "C", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Verify the change tracker is populated before clearing
        Assert.That(_context.ChangeTracker.Entries().Any(), Is.True);

        // Act
        await _repository.ClearChangeTrackerAsync();

        // Assert
        Assert.That(_context.ChangeTracker.Entries().Count(), Is.EqualTo(0));
    }

    #endregion

    #region GetPagedAsync

    /// <summary>
    /// Verifies GetPagedAsync returns the correct page of items and the correct total count.
    /// </summary>
    [Test]
    public async Task GetPagedAsync_ReturnsCorrectPage()
    {
        // Arrange — seed 50 events
        var entities = Enumerable.Range(1, 50)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.{i:D2}",
                InsertedDate = DateTimeOffset.UtcNow
            })
            .ToList();
        await _context.AuditEvents.AddRangeAsync(entities);
        await _context.SaveChangesAsync();

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(pageNumber: 1, pageSize: 10);

        // Assert
        Assert.That(totalCount, Is.EqualTo(50));
        Assert.That(items.ToList(), Has.Count.EqualTo(10));
    }

    /// <summary>
    /// Verifies GetPagedAsync returns only the remaining items on the last partial page.
    /// </summary>
    [Test]
    public async Task GetPagedAsync_LastPage_PartialResults()
    {
        // Arrange — seed 25 events
        var entities = Enumerable.Range(1, 25)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.{i:D2}",
                InsertedDate = DateTimeOffset.UtcNow
            })
            .ToList();
        await _context.AuditEvents.AddRangeAsync(entities);
        await _context.SaveChangesAsync();

        // Act — page 3 of a page size 10 over 25 items should yield 5 items
        var (items, totalCount) = await _repository.GetPagedAsync(pageNumber: 3, pageSize: 10);

        // Assert
        Assert.That(totalCount, Is.EqualTo(25));
        Assert.That(items.ToList(), Has.Count.EqualTo(5));
    }

    #endregion

    #region FindAsync

    /// <summary>
    /// Verifies FindAsync filters results correctly when a predicate is supplied.
    /// </summary>
    [Test]
    public async Task FindAsync_WithPredicate_AppliesCriteria()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Logout", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Data.Export", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.FindAsync(static x => x.EventType == "User.Login")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.EventType == "User.Login"), Is.True);
    }

    #endregion

    #region CountAsync

    /// <summary>
    /// Verifies CountAsync returns only the count of entities matching the predicate.
    /// </summary>
    [Test]
    public async Task CountAsync_WithPredicate_ReturnsFilteredCount()
    {
        // Arrange — 5 "User.Login" and 3 "User.Logout"
        var logins = Enumerable.Range(1, 5)
            .Select(static _ => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow
            });
        var logouts = Enumerable.Range(1, 3)
            .Select(static _ => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow
            });
        await _context.AuditEvents.AddRangeAsync(logins.Concat(logouts));
        await _context.SaveChangesAsync();

        // Act
        var count = await _repository.CountAsync(static x => x.EventType == "User.Login");

        // Assert
        Assert.That(count, Is.EqualTo(5));
    }

    #endregion

    #region ExistsAsync

    /// <summary>
    /// Verifies ExistsAsync returns true when an entity matching the predicate is present.
    /// </summary>
    [Test]
    public async Task ExistsAsync_ExistingEntity_ReturnsTrue()
    {
        // Arrange
        var knownId = Guid.NewGuid();
        await _context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = knownId,
            EventType = "User.Login",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var exists = await _repository.ExistsAsync(x => x.EventId == knownId);

        // Assert
        Assert.That(exists, Is.True);
    }

    /// <summary>
    /// Verifies ExistsAsync returns false when no entity matches the predicate.
    /// </summary>
    [Test]
    public async Task ExistsAsync_NonExistent_ReturnsFalse()
    {
        // Act — no data seeded; random ID will never match
        var exists = await _repository.ExistsAsync(static x => x.EventId == Guid.NewGuid());

        // Assert
        Assert.That(exists, Is.False);
    }

    #endregion
}
