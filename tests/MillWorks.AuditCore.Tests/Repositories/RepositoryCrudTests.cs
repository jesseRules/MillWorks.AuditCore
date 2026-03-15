using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for base Repository&lt;T&gt; CRUD methods not covered by existing test files.
/// Uses AuditEventRepository (extends Repository) with InMemory DB.
/// </summary>
[TestFixture]
public class RepositoryCrudTests
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

    #region FindAsync

    /// <summary>
    /// Verifies FindAsync returns entities matching the predicate.
    /// </summary>
    [Test]
    public async Task FindAsync_WithMatchingPredicate_ReturnsMatchingEntities()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Logout", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.FindAsync(static e => e.EventType == "User.Login")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.EventType == "User.Login"), Is.True);
    }

    /// <summary>
    /// Verifies FindAsync returns empty when no entities match.
    /// </summary>
    [Test]
    public async Task FindAsync_WithNoMatches_ReturnsEmpty()
    {
        // Arrange
        await _context.AuditEvents.AddAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.FindAsync(static e => e.EventType == "NonExistent")).ToList();

        // Assert
        Assert.That(results, Is.Empty);
    }

    #endregion

    #region FirstOrDefaultAsync

    /// <summary>
    /// Verifies FirstOrDefaultAsync returns the first matching entity.
    /// </summary>
    [Test]
    public async Task FirstOrDefaultAsync_WithMatch_ReturnsEntity()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Target",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.FirstOrDefaultAsync(e => e.EventId == entity.EventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventType, Is.EqualTo("Target"));
    }

    /// <summary>
    /// Verifies FirstOrDefaultAsync returns null when no match exists.
    /// </summary>
    [Test]
    public async Task FirstOrDefaultAsync_WithNoMatch_ReturnsNull()
    {
        // Act
        var result = await _repository.FirstOrDefaultAsync(e => e.EventId == Guid.NewGuid());

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region CountAsync

    /// <summary>
    /// Verifies CountAsync without predicate returns total count.
    /// </summary>
    [Test]
    public async Task CountAsync_WithoutPredicate_ReturnsTotalCount()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "C", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var count = await _repository.CountAsync();

        // Assert
        Assert.That(count, Is.EqualTo(3));
    }

    /// <summary>
    /// Verifies CountAsync with predicate returns filtered count.
    /// </summary>
    [Test]
    public async Task CountAsync_WithPredicate_ReturnsFilteredCount()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var count = await _repository.CountAsync(static e => e.EventType == "A");

        // Assert
        Assert.That(count, Is.EqualTo(2));
    }

    #endregion

    #region AddRangeAsync

    /// <summary>
    /// Verifies AddRangeAsync persists multiple entities.
    /// </summary>
    [Test]
    public async Task AddRangeAsync_WithMultipleEntities_PersistsAll()
    {
        // Arrange
        var entities = new[]
        {
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Batch1", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Batch2", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Batch3", InsertedDate = DateTimeOffset.UtcNow }
        };

        // Act
        var result = (await _repository.AddRangeAsync(entities)).ToList();
        await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        var dbCount = await _context.AuditEvents.CountAsync();
        Assert.That(dbCount, Is.EqualTo(3));
    }

    #endregion

    #region UpdateRangeAsync

    /// <summary>
    /// Verifies UpdateRangeAsync modifies multiple tracked entities.
    /// </summary>
    [Test]
    public async Task UpdateRangeAsync_WithModifiedEntities_PersistsChanges()
    {
        // Arrange
        var entities = new[]
        {
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Original1", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Original2", InsertedDate = DateTimeOffset.UtcNow }
        };
        await _context.AuditEvents.AddRangeAsync(entities);
        await _context.SaveChangesAsync();

        // Act
        entities[0].EventType = "Updated1";
        entities[1].EventType = "Updated2";
        var result = (await _repository.UpdateRangeAsync(entities)).ToList();
        await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        var updated0 = await _context.AuditEvents.FindAsync(entities[0].EventId);
        var updated1 = await _context.AuditEvents.FindAsync(entities[1].EventId);
        Assert.That(updated0!.EventType, Is.EqualTo("Updated1"));
        Assert.That(updated1!.EventType, Is.EqualTo("Updated2"));
    }

    #endregion

    // ExecuteDeleteWhereAsync test moved to Integration/RepositoryBaseIntegrationTests.cs

    #region GetPagedAsync edge cases

    /// <summary>
    /// Verifies GetPagedAsync throws for invalid page number.
    /// </summary>
    [Test]
    public void GetPagedAsync_WithZeroPageNumber_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await _repository.GetPagedAsync(0, 10));
    }

    /// <summary>
    /// Verifies GetPagedAsync throws for invalid page size.
    /// </summary>
    [Test]
    public void GetPagedAsync_WithZeroPageSize_ThrowsArgumentException()
    {
        Assert.ThrowsAsync<ArgumentException>(async () => await _repository.GetPagedAsync(1, 0));
    }

    /// <summary>
    /// Verifies GetPagedAsync returns empty items for page beyond data.
    /// </summary>
    [Test]
    public async Task GetPagedAsync_BeyondLastPage_ReturnsEmptyItems()
    {
        // Arrange
        await _context.AuditEvents.AddAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Only", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(10, 10);

        // Assert
        Assert.That(totalCount, Is.EqualTo(1));
        Assert.That(items.ToList(), Is.Empty);
    }

    /// <summary>
    /// Verifies GetPagedAsync applies ordering.
    /// </summary>
    [Test]
    public async Task GetPagedAsync_WithOrdering_ReturnsOrderedItems()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "C", InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-2) });
        await _context.SaveChangesAsync();

        // Act
        var (items, _) = await _repository.GetPagedAsync(1, 10,
            orderBy: static q => q.OrderBy(static e => e.EventType));
        var itemList = items.ToList();

        // Assert
        Assert.That(itemList[0].EventType, Is.EqualTo("A"));
        Assert.That(itemList[1].EventType, Is.EqualTo("B"));
        Assert.That(itemList[2].EventType, Is.EqualTo("C"));
    }

    #endregion

    #region GetQueryable

    /// <summary>
    /// Verifies GetQueryable returns a working queryable.
    /// </summary>
    [Test]
    public async Task GetQueryable_ReturnsWorkingQueryable()
    {
        // Arrange
        await _context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "A", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "B", InsertedDate = DateTimeOffset.UtcNow });
        await _context.SaveChangesAsync();

        // Act
        var queryable = _repository.GetQueryable();
        var count = await queryable.CountAsync();

        // Assert
        Assert.That(count, Is.EqualTo(2));
    }

    #endregion

    #region SaveChangesAsync

    /// <summary>
    /// Verifies SaveChangesAsync persists pending changes and returns affected count.
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_WithPendingChanges_ReturnsAffectedCount()
    {
        // Arrange
        await _repository.AddAsync(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "SaveTest",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Act
        var affected = await _repository.SaveChangesAsync();

        // Assert
        Assert.That(affected, Is.GreaterThan(0));
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    #endregion

    #region GetAllAsync

    /// <summary>
    /// Verifies GetAllAsync returns empty when table is empty.
    /// </summary>
    [Test]
    public async Task GetAllAsync_EmptyTable_ReturnsEmpty()
    {
        // Act
        var results = await _repository.GetAllAsync();

        // Assert
        Assert.That(results.ToList(), Is.Empty);
    }

    #endregion
}
