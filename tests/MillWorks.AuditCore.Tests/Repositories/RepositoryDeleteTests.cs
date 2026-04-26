using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for Repository&lt;T&gt;.DeleteAsync overloads — both the original (no user context)
/// and the new overloads that accept a deletedBy parameter.
/// </summary>
[TestFixture]
public class RepositoryDeleteTests
{
    private DbContextOptions<AuditDbContext> _options;
    private AuditDbContext _context;
    private AuditEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(_options);
        _repository = new AuditEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region Original DeleteAsync (no deletedBy)

    /// <summary>
    /// Verifies that the original DeleteAsync(entity) sets IsDeleted and DeletedAt but NOT DeletedById.
    /// </summary>
    [Test]
    public async Task DeleteAsync_Entity_SetsIsDeletedAndDeletedAt_ButNotDeletedById()
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
        Assert.That(deleted.DeletedById, Is.Null);
    }

    /// <summary>
    /// Verifies that the original DeleteAsync(id) soft-deletes when entity exists.
    /// </summary>
    [Test]
    public async Task DeleteAsync_ById_SoftDeletesExistingEntity()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.DeleteById",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(entity.EventId);
        await _repository.SaveChangesAsync();

        // Assert
        var deleted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(deleted, Is.Not.Null);
        Assert.That(deleted!.IsDeleted, Is.True);
    }

    /// <summary>
    /// Verifies that DeleteAsync(id) is a no-op for a non-existent ID.
    /// </summary>
    [Test]
    public async Task DeleteAsync_ById_WithNonExistentId_DoesNothing()
    {
        // Act — should not throw
        await _repository.DeleteAsync(Guid.NewGuid());
        await _repository.SaveChangesAsync();

        // Assert
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(0));
    }

    #endregion

    #region New DeleteAsync with deletedBy

    /// <summary>
    /// Verifies that DeleteAsync(entity, deletedBy) calls AuditAggregateRoot.Delete(deletedBy),
    /// which sets IsDeleted, DeletedAt, AND DeletedById.
    /// </summary>
    [Test]
    public async Task DeleteAsync_EntityWithDeletedBy_SetsAllDeleteFields()
    {
        // Arrange
        var deletedBy = Guid.NewGuid();
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.DeleteWithUser",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(entity, deletedBy);
        await _repository.SaveChangesAsync();

        // Assert
        var deleted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(deleted, Is.Not.Null);
        Assert.That(deleted!.IsDeleted, Is.True);
        Assert.That(deleted.DeletedAt, Is.Not.Null);
        Assert.That(deleted.DeletedById, Is.EqualTo(deletedBy));
    }

    /// <summary>
    /// Verifies that DeleteAsync(entity, deletedBy) raises a domain event.
    /// </summary>
    [Test]
    public async Task DeleteAsync_EntityWithDeletedBy_RaisesDomainEvent()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.DomainEvent",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(entity, Guid.NewGuid());

        // Assert
        Assert.That(entity.DomainEvents, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Verifies that DeleteAsync(id, deletedBy) soft-deletes with the correct user.
    /// </summary>
    [Test]
    public async Task DeleteAsync_ByIdWithDeletedBy_SetsDeletedById()
    {
        // Arrange
        var deletedBy = Guid.NewGuid();
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.DeleteByIdWithUser",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(entity);
        await _context.SaveChangesAsync();

        // Act
        await _repository.DeleteAsync(entity.EventId, deletedBy);
        await _repository.SaveChangesAsync();

        // Assert
        var deleted = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(deleted, Is.Not.Null);
        Assert.That(deleted!.IsDeleted, Is.True);
        Assert.That(deleted.DeletedById, Is.EqualTo(deletedBy));
    }

    /// <summary>
    /// Verifies that DeleteAsync(id, deletedBy) is a no-op for a non-existent ID.
    /// </summary>
    [Test]
    public async Task DeleteAsync_ByIdWithDeletedBy_NonExistentId_DoesNothing()
    {
        // Act — should not throw
        await _repository.DeleteAsync(Guid.NewGuid(), Guid.NewGuid());
        await _repository.SaveChangesAsync();

        // Assert
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(0));
    }

    #endregion
}
