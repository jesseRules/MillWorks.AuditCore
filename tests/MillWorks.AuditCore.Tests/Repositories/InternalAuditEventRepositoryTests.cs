using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for InternalAuditEventRepository add/save/exists operations.
/// Validates the simplified SaveChangesAsync that relies on AuditApplicationDbContext bypass.
/// </summary>
[TestFixture]
public class InternalAuditEventRepositoryNewTests
{
    private DbContextOptions<AuditApplicationDbContext> _options;
    private AuditApplicationDbContext _context;
    private InternalAuditEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(_options);
        _repository = new InternalAuditEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    #region AddAsync

    /// <summary>
    /// Verifies AddAsync persists an audit event.
    /// </summary>
    [Test]
    public async Task AddAsync_PersistsEntity()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Internal.Test",
            InsertedDate = DateTimeOffset.UtcNow
        };

        // Act
        var result = await _repository.AddAsync(entity);
        await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result.EventId, Is.EqualTo(entity.EventId));
        var saved = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// Verifies AddAsync with null entity throws.
    /// </summary>
    [Test]
    public void AddAsync_NullEntity_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _repository.AddAsync(null!));
    }

    #endregion

    #region AddRangeAsync

    /// <summary>
    /// Verifies AddRangeAsync persists multiple entities.
    /// </summary>
    [Test]
    public async Task AddRangeAsync_PersistsMultipleEntities()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Test1", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test2", InsertedDate = DateTimeOffset.UtcNow }
        };

        // Act
        await _repository.AddRangeAsync(events);
        await _repository.SaveChangesAsync();

        // Assert
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies AddRangeAsync with null throws.
    /// </summary>
    [Test]
    public void AddRangeAsync_NullEntities_ThrowsArgumentNullException()
    {
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _repository.AddRangeAsync(null!));
    }

    #endregion

    #region SaveChangesAsync

    /// <summary>
    /// Verifies that the simplified SaveChangesAsync still works correctly.
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_PersistsChanges()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Save.Test",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _repository.AddAsync(entity);

        // Act
        var result = await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Is.GreaterThan(0));
        var saved = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// Verifies AutoDetectChangesEnabled is NOT toggled by the simplified implementation.
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_DoesNotToggleAutoDetectChanges()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Toggle.Test",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _repository.AddAsync(entity);

        var initialState = _context.ChangeTracker.AutoDetectChangesEnabled;

        // Act
        await _repository.SaveChangesAsync();

        // Assert — state should be unchanged (no toggling)
        Assert.That(_context.ChangeTracker.AutoDetectChangesEnabled, Is.EqualTo(initialState));
    }

    /// <summary>
    /// Verifies multiple sequential saves work.
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_MultipleSequentialSaves_WorkCorrectly()
    {
        // Act
        await _repository.AddAsync(new AuditEventEntity
        {
            EventId = Guid.NewGuid(), EventType = "First", InsertedDate = DateTimeOffset.UtcNow
        });
        await _repository.SaveChangesAsync();

        await _repository.AddAsync(new AuditEventEntity
        {
            EventId = Guid.NewGuid(), EventType = "Second", InsertedDate = DateTimeOffset.UtcNow
        });
        await _repository.SaveChangesAsync();

        // Assert
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    #endregion

    #region ExistsAsync

    /// <summary>
    /// Verifies ExistsAsync returns true for existing event.
    /// </summary>
    [Test]
    public async Task ExistsAsync_ExistingEvent_ReturnsTrue()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        await _context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId, EventType = "Exists", InsertedDate = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();

        // Act
        var exists = await _repository.ExistsAsync(eventId);

        // Assert
        Assert.That(exists, Is.True);
    }

    /// <summary>
    /// Verifies ExistsAsync returns false for non-existent event.
    /// </summary>
    [Test]
    public async Task ExistsAsync_NonExistent_ReturnsFalse()
    {
        // Act
        var exists = await _repository.ExistsAsync(Guid.NewGuid());

        // Assert
        Assert.That(exists, Is.False);
    }

    #endregion

    #region GetByIdAsync

    /// <summary>
    /// Verifies GetByIdAsync returns the correct entity without tracking.
    /// </summary>
    [Test]
    public async Task GetByIdAsync_ReturnsDetachedEntity()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        await _context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId, EventType = "GetById", InsertedDate = DateTimeOffset.UtcNow
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _repository.GetByIdAsync(eventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventId, Is.EqualTo(eventId));
        Assert.That(_context.Entry(result).State, Is.EqualTo(EntityState.Detached));
    }

    #endregion

    #region Constructor

    /// <summary>
    /// Verifies null context throws.
    /// </summary>
    [Test]
    public void Constructor_NullContext_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(static () =>
            new InternalAuditEventRepository(null!));
    }

    #endregion
}
