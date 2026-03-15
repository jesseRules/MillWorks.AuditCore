using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// InternalAuditEventRepository tests
/// </summary>
[TestFixture]
public class InternalAuditEventRepositoryTests
{
    /// <summary>
    /// Options for the in-memory database
    /// </summary>
    private DbContextOptions<AuditApplicationDbContext> _options;

    /// <summary>
    /// Context for the in-memory database
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Repository under test
    /// </summary>
    private InternalAuditEventRepository _repository;

    /// <summary>
    /// Setup method to initialize in-memory database and repository
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(_options);
        _repository = new InternalAuditEventRepository(_context);
    }

    /// <summary>
    /// Tear down method to dispose resources
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    /// <summary>
    /// AddAsync adds an entity successfully
    /// </summary>
    [Test]
    public async Task AddAsync_AddsEntitySuccessfully()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow,
            User = "test@example.com"
        };

        // Act
        var result = await _repository.AddAsync(auditEvent);
        await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventId, Is.EqualTo(auditEvent.EventId));

        var saved = await _context.AuditEvents.FindAsync(auditEvent.EventId);
        Assert.That(saved, Is.Not.Null);
        Assert.That(saved!.EventType, Is.EqualTo("Test.Event"));
    }

    /// <summary>
    /// AddRangeAsync adds multiple entities successfully
    /// </summary>
    [Test]
    public async Task AddRangeAsync_AddsMultipleEntitiesSuccessfully()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Test.Event1",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Test.Event2",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        // Act
        await _repository.AddRangeAsync(events);
        await _repository.SaveChangesAsync();

        // Assert
        foreach (var evt in events)
        {
            var saved = await _context.AuditEvents.FindAsync(evt.EventId);
            Assert.That(saved, Is.Not.Null);
        }
    }

    /// <summary>
    /// SaveChangesAsync disables and restores AutoDetectChangesEnabled
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_DisablesAndRestoresAutoDetectChanges()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _repository.AddAsync(auditEvent);

        // Verify AutoDetectChangesEnabled starts as true
        var initialState = _context.ChangeTracker.AutoDetectChangesEnabled;
        Assert.That(initialState, Is.True);

        // Act
        var result = await _repository.SaveChangesAsync();

        // Assert - AutoDetectChangesEnabled should be restored after save
        Assert.That(_context.ChangeTracker.AutoDetectChangesEnabled, Is.True);
        Assert.That(result, Is.GreaterThan(0));

        var saved = await _context.AuditEvents.FindAsync(auditEvent.EventId);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// GetByIdAsync returns the correct entity
    /// </summary>
    [Test]
    public async Task GetByIdAsync_ReturnsCorrectEntity()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetByIdAsync(auditEvent.EventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventId, Is.EqualTo(auditEvent.EventId));
        Assert.That(result.EventType, Is.EqualTo("Test.Event"));
    }

    /// <summary>
    /// GetByIdAsync with non-existent ID returns null
    /// </summary>
    [Test]
    public async Task GetByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await _repository.GetByIdAsync(nonExistentId);

        // Assert
        Assert.That(result, Is.Null);
    }

    /// <summary>
    /// GetByIdAsync does not track the entity
    /// </summary>
    [Test]
    public async Task GetByIdAsync_DoesNotTrackEntity()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Act
        var result = await _repository.GetByIdAsync(auditEvent.EventId);

        // Assert
        Assert.That(result, Is.Not.Null);

        var entry = _context.Entry(result!);
        Assert.That(entry.State, Is.EqualTo(EntityState.Detached));
    }

    /// <summary>
    /// ExistsAsync returns true for existing event
    /// </summary>
    [Test]
    public async Task ExistsAsync_WithExistingEvent_ReturnsTrue()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var exists = await _repository.ExistsAsync(eventId);

        // Assert
        Assert.That(exists, Is.True);
    }

    /// <summary>
    /// ExistsAsync returns false for non-existent event
    /// </summary>
    [Test]
    public async Task ExistsAsync_WithNonExistentEvent_ReturnsFalse()
    {
        // Arrange
        var eventId = Guid.NewGuid();

        // Act
        var exists = await _repository.ExistsAsync(eventId);

        // Assert
        Assert.That(exists, Is.False);
    }

    /// <summary>
    /// Constructor with null context throws ArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullContext_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(static () =>
            new InternalAuditEventRepository(null!));
    }

    /// <summary>
    /// AddAsync with null entity throws ArgumentNullException
    /// </summary>
    [Test]
    public void AddAsync_WithNullEntity_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _repository.AddAsync(null!));
    }

    /// <summary>
    /// AddRangeAsync with null entities throws ArgumentNullException
    /// </summary>
    [Test]
    public void AddRangeAsync_WithNullEntities_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await _repository.AddRangeAsync(null!));
    }

    /// <summary>
    /// Multiple save operations work correctly
    /// </summary>
    [Test]
    public async Task MultipleSaveOperations_WorkCorrectly()
    {
        // Arrange
        var event1 = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event1",
            InsertedDate = DateTimeOffset.UtcNow
        };

        var event2 = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event2",
            InsertedDate = DateTimeOffset.UtcNow
        };

        // Act
        await _repository.AddAsync(event1);
        await _repository.SaveChangesAsync();

        await _repository.AddAsync(event2);
        await _repository.SaveChangesAsync();

        // Assert
        var saved1 = await _context.AuditEvents.FindAsync(event1.EventId);
        var saved2 = await _context.AuditEvents.FindAsync(event2.EventId);

        Assert.That(saved1, Is.Not.Null);
        Assert.That(saved2, Is.Not.Null);
    }

    /// <summary>
    /// SaveChangesAsync with CancellationToken propagates the token
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_WithCancellationToken_PropagatesToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _repository.AddAsync(auditEvent, cts.Token);

        // Act
        await _repository.SaveChangesAsync(cts.Token);

        // Assert
        var saved = await _context.AuditEvents.FindAsync(auditEvent.EventId, cts.Token);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// SaveChangesAsync returns number of affected rows
    /// </summary>
    [Test]
    public async Task SaveChangesAsync_ReturnsNumberOfAffectedRows()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event1", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event2", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Test.Event3", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _repository.AddRangeAsync(events);

        // Act
        var result = await _repository.SaveChangesAsync();

        // Assert
        Assert.That(result, Is.EqualTo(3));
    }
}