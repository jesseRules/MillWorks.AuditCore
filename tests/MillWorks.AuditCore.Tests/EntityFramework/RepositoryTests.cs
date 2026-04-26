using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for the generic Repository base class using AuditEventRepository with InMemory DB
/// </summary>
[TestFixture]
public class RepositoryTests
{
    /// <summary>
    /// Options for the in-memory database
    /// </summary>
    private DbContextOptions<AuditDbContext> _options;

    /// <summary>
    /// Context for the in-memory database
    /// </summary>
    private AuditDbContext _context;

    /// <summary>
    /// Repository under test (AuditEventRepository extends Repository)
    /// </summary>
    private AuditEventRepository _repository;

    /// <summary>
    /// Setup method to initialize in-memory database and repository
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(_options);
        _repository = new AuditEventRepository(_context);
    }

    /// <summary>
    /// Tear down method to dispose resources
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region Basic CRUD

    /// <summary>
    /// Verifies that AddAsync persists an entity to the database
    /// </summary>
    [Test]
    public async Task AddAsync_WithValidEntity_PersistsToDatabase()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Created",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "test@example.com"
        };

        // Act
        var result = await _repository.AddAsync(entity);
        await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventId, Is.EqualTo(entity.EventId));

        var saved = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.EventType, Is.EqualTo("Test.Created"));
    }

    /// <summary>
    /// Verifies that GetByIdAsync returns an existing entity
    /// </summary>
    [Test]
    public async Task GetByIdAsync_WithExistingId_ReturnsEntity()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "user@test.com"
        };

        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetByIdAsync(entity.EventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventId, Is.EqualTo(entity.EventId));
        Assert.That(result.EventType, Is.EqualTo("Test.Event"));
    }

    /// <summary>
    /// Verifies that GetByIdAsync returns null for a non-existent ID
    /// </summary>
    [Test]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByIdAsync(Guid.NewGuid());

        // Assert
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// Verifies that DeleteAsync soft-deletes an AuditAggregateRoot entity
    /// </summary>
    [Test]
    public async Task DeleteAsync_WithSoftDeletableEntity_SetsIsDeletedTrue()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Delete",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(entity);
        await _repository.SaveChangesAsync();

        // Assert
        var deleted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(deleted, Is.Not.Null);
        Assert.That(deleted!.IsDeleted, Is.True);
        Assert.That(deleted.DeletedAt, Is.Not.Null);
    }

    /// <summary>
    /// Verifies that DeleteRangeAsync soft-deletes all entities
    /// </summary>
    [Test]
    public async Task DeleteRangeAsync_WithMultipleEntities_DeletesAll()
    {
        // Arrange
        var entities = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Test.Delete1", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Delete2", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Delete3", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(entities);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteRangeAsync(entities);
        await _repository.SaveChangesAsync();

        // Assert
        foreach (var entity in entities)
        {
            var deleted = await _context.AuditEvents.FindAsync(entity.EventId);
            Assert.That(deleted, Is.Not.Null);
            Assert.That(deleted!.IsDeleted, Is.True);
        }
    }

    /// <summary>
    /// Verifies that UpdateAsync persists modifications
    /// </summary>
    [Test]
    public async Task UpdateAsync_WithModifiedEntity_PersistsChanges()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Original",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "original@test.com"
        };

        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        entity.EventType = "Test.Updated";
        entity.User = "updated@test.com";
        await _repository.UpdateAsync(entity);
        await _repository.SaveChangesAsync();

        // Assert
        var updated = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.EventType, Is.EqualTo("Test.Updated"));
        Assert.That(updated.User, Is.EqualTo("updated@test.com"));
    }

    #endregion

    #region Pagination

    /// <summary>
    /// Verifies that GetPagedAsync returns correctly paginated results
    /// </summary>
    [Test]
    public async Task GetPagedAsync_WithValidParams_ReturnsPaginatedResults()
    {
        // Arrange - seed 25 entities
        for (int i = 0; i < 25; i++)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Test.Paged",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }

        await _context.SaveChangesAsync();

        // Act - page 2, size 10
        var (items, totalCount) = await _repository.GetPagedAsync(2, 10);

        // Assert
        var itemList = items.ToList();
        Assert.That(itemList, Has.Count.EqualTo(10));
        Assert.That(totalCount, Is.EqualTo(25));
    }

    /// <summary>
    /// Verifies that GetPagedAsync with predicate filter works correctly
    /// </summary>
    [Test]
    public async Task GetPagedAsync_WithPredicateFilter_FiltersCorrectly()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow
            });
        }

        for (int i = 0; i < 10; i++)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(
            1, 10,
            predicate: static e => e.EventType == "User.Login");

        // Assert
        var itemList = items.ToList();
        Assert.That(totalCount, Is.EqualTo(15));
        Assert.That(itemList, Has.Count.EqualTo(10));
        Assert.That(itemList.All(static e => e.EventType == "User.Login"), Is.True);
    }

    /// <summary>
    /// Verifies that GetPagedAsync returns correct TotalCount matching full dataset
    /// </summary>
    [Test]
    public async Task GetPagedAsync_ReturnsCorrectTotalCount()
    {
        // Arrange
        for (int i = 0; i < 17; i++)
        {
            await _context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Test.Count",
                InsertedDate = DateTimeOffset.UtcNow
            });
        }

        await _context.SaveChangesAsync();

        // Act
        var (items, totalCount) = await _repository.GetPagedAsync(3, 5);

        // Assert
        Assert.That(totalCount, Is.EqualTo(17));
        // Page 3 of size 5 with 17 items => last page has 7 remaining, page 3 has items 10-14 = 5 items
        var itemList = items.ToList();
        Assert.That(itemList, Has.Count.EqualTo(5));
    }

    #endregion

    #region Query helpers

    /// <summary>
    /// Verifies that GetAllAsync returns all entities
    /// </summary>
    [Test]
    public async Task GetAllAsync_ReturnsAllEntities()
    {
        // Arrange
        var entities = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event1", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event2", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event3", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(entities);
        await _context.SaveChangesAsync();

        // Act
        var results = await _repository.GetAllAsync();

        // Assert
        var resultList = results.ToList();
        Assert.That(resultList, Has.Count.EqualTo(3));
    }

    /// <summary>
    /// Verifies that ExistsAsync returns true for an existing entity
    /// </summary>
    [Test]
    public async Task ExistsAsync_WithExistingEntity_ReturnsTrue()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Exists",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        var exists = await _repository.ExistsAsync(e => e.EventId == entity.EventId);

        // Assert
        Assert.That(exists, Is.True);
    }

    /// <summary>
    /// Verifies that ExistsAsync returns false for a non-existent entity
    /// </summary>
    [Test]
    public async Task ExistsAsync_WithNonExistentEntity_ReturnsFalse()
    {
        // Act
        var exists = await _repository.ExistsAsync(static e => e.EventId == Guid.NewGuid());

        // Assert
        Assert.That(exists, Is.False);
    }

    #endregion
}
