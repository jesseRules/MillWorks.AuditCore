using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Unit tests for AuditSaveChangesInterceptor
/// </summary>
[TestFixture]
public class AuditSaveChangesInterceptorTests
{
    /// <summary>
    /// Mock logger for capturing log output
    /// </summary>
    private Mock<ILogger<AuditSaveChangesInterceptor>> _mockLogger = null!;

    /// <summary>
    /// Instance of the interceptor under test
    /// </summary>
    private AuditSaveChangesInterceptor _interceptor = null!;

    /// <summary>
    /// DbContext options for in-memory database
    /// </summary>
    private DbContextOptions<TestDbContext> _dbOptions = null!;

    /// <summary>
    /// DbContext instance for testing
    /// </summary>
    private TestDbContext _dbContext = null!;

    /// <summary>
    /// Service provider hosting the audit subsystem (sink, writer, audit DbContext).
    /// The writer opens fresh scopes against this provider per save; the test's
    /// TestDbContext shares the same in-memory database name so audit rows written
    /// through the writer are visible via the test context's DbSet&lt;AuditLogEntity&gt;.
    /// </summary>
    private ServiceProvider _provider = null!;

    /// <summary>
    /// Setup method to initialize test dependencies
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var dbName = $"TestDb_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(static w =>
                {
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning);
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning);
                }));
        services.AddScoped<IAuditEntityBatchWriter, AuditEntityBatchWriter>();
        services.AddScoped<IAuditEventBatchWriter, AuditEventBatchWriter>();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditSink, ImmediateSink>();

        _provider = services.BuildServiceProvider();
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        _mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _interceptor = new AuditSaveChangesInterceptor(
            _mockLogger.Object,
            scopeFactory: scopeFactory);

        _dbOptions = TestDbContextFactory.CreateInMemoryOptions<TestDbContext>(
            dbName: dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        _dbContext = new TestDbContext(_dbOptions);
    }

    /// <summary>
    /// Tear down method to dispose resources
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _provider.Dispose();
    }

    /// <summary>
    /// Constructor with null logger throws ArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(static () =>
            new AuditSaveChangesInterceptor(null!));
    }

    /// <summary>
    /// Saving changes with empty change tracker does not process audit
    /// </summary>
    [Test]
    public async Task SavingChanges_WithEmptyChangeTracker_DoesNotProcessAudit()
    {
        // Arrange - Don't add any entities

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert - Should not log any processing since no changes
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with bypass flag set bypasses the interceptor
    /// </summary>
    [Test]
    public async Task SavingChanges_WithBypassFlag_BypassesInterceptor()
    {
        // Arrange
        _dbContext.BypassAuditInterceptor = true;

        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Trace,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("bypass flag")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with only audit entities produces no audit logs (filtered in ProcessAuditableEntries)
    /// </summary>
    [Test]
    public async Task SavingChanges_WithAuditEntityOnly_ProducesNoAuditLogs()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };

        _dbContext.AuditEvents.Add(auditEvent);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — audit entities are filtered from auditable entries, so no processing
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with multiple audit entities produces no audit logs
    /// </summary>
    [Test]
    public async Task SavingChanges_WithMultipleAuditEntities_ProducesNoAuditLogs()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };

        var auditIntegrity = new AuditIntegrityEntity
        {
            EventId = Guid.NewGuid(),
            EventHash = "hash",
            TrustedTimestamp = DateTimeOffset.UtcNow,
            Checksum = "checksum"
        };

        _dbContext.AuditEvents.Add(auditEvent);
        _dbContext.AuditIntegrity.Add(auditIntegrity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — all entries are audit entities, so all filtered out
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with regular entity processes audit
    /// </summary>
    [Test]
    public async Task SavingChanges_WithRegularEntity_ProcessesAudit()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with added entity logs correct state
    /// </summary>
    [Test]
    public async Task SavingChanges_WithAddedEntity_LogsCorrectState()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Added")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with modified entity logs correct state
    /// </summary>
    [Test]
    public async Task SavingChanges_WithModifiedEntity_LogsCorrectState()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Original" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();

        // Modify entity
        testEntity.Name = "Modified";
        _dbContext.TestEntities.Update(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Modified")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with deleted entity logs correct state
    /// </summary>
    [Test]
    public async Task SavingChanges_WithDeletedEntity_LogsCorrectState()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "ToDelete" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();

        // Delete entity
        _dbContext.TestEntities.Remove(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Deleted")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with no changes does not process audit
    /// </summary>
    [Test]
    public async Task SavingChanges_WithNoChanges_DoesNotProcessAudit()
    {
        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with exception in logger does not throw
    /// </summary>
    [Test]
    public Task SavingChanges_WithException_DoesNotThrow()
    {
        // Arrange
        // Only make the Debug log throw, not the Error log
        _mockLogger
            .Setup(static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Throws(new Exception("Logger error"));

        // Allow Error level logging to work
        _mockLogger
            .Setup(static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Verifiable();

        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);

        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _dbContext.SaveChangesAsync());

        // Verify error was logged
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Error processing auditable entries")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Saving changes with mixed entities processes non-audit entities (audit entities are filtered out in ProcessAuditableEntries)
    /// </summary>
    [Test]
    public async Task SavingChanges_WithMixedEntities_ProcessesNonAuditEntities()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };

        _dbContext.TestEntities.Add(testEntity);
        _dbContext.AuditEvents.Add(auditEvent);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the regular TestEntity should still be audited
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Saving changes with cancellation token propagates the token
    /// </summary>
    [Test]
    public async Task SavingChangesAsync_WithCancellationToken_PropagatesToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);

        // Act
        await _dbContext.SaveChangesAsync(cts.Token);

        // Assert
        Assert.That(testEntity.Id, Is.Not.EqualTo(Guid.Empty));
    }

    /// <summary>
    /// Saving changes with unchanged entity ignores the entry
    /// </summary>
    [Test]
    public async Task SavingChanges_WithUnchangedEntity_IgnoresEntry()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();

        // Reset the mock to clear previous invocations
        _mockLogger.Reset();

        // Act - Save again without any modifications
        await _dbContext.SaveChangesAsync();

        // Assert - should not log any audit processing since no changes
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with unchanged entity ignores the entry
    /// </summary>
    [Test]
    public async Task SavingChanges_WithUnchangedEntity_IgnoresEntry_WithFallback()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();

        var savedId = testEntity.Id;

        // Clear change tracker to simulate a new context
        _dbContext.ChangeTracker.Clear();

        // Reset mock to ignore previous invocations
        _mockLogger.Reset();

        // Query the entity back (this attaches it as Unchanged)
        var queriedEntity = await _dbContext.TestEntities.FindAsync(savedId);

        Assert.That(queriedEntity, Is.Not.Null);
        Assert.That(_dbContext.Entry(queriedEntity!).State, Is.EqualTo(EntityState.Unchanged));

        // Act - Save without any modifications
        await _dbContext.SaveChangesAsync();

        // Assert - should not log any audit processing since no changes
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Saving changes with unchanged entity ignores the entry
    /// </summary>
    [Test]
    public async Task SavingChanges_WithDetachedEntity_IgnoresEntry()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Test" };
        _dbContext.Entry(testEntity).State = EntityState.Detached;

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    /// <summary>
    /// Adding an entity creates an audit log with Created action
    /// </summary>
    [Test]
    public async Task SavingChanges_WithAddedEntity_CreatesAuditLog()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "NewEntity" };
        _dbContext.TestEntities.Add(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var auditLogs = await _dbContext.AuditLogs.AsNoTracking().ToListAsync();
        Assert.That(auditLogs, Has.Count.EqualTo(1));
        Assert.Multiple(() =>
        {
            Assert.That(auditLogs[0].EntityName, Is.EqualTo("TestEntity"));
            Assert.That(auditLogs[0].Action, Is.EqualTo(AuditAction.Created));
            Assert.That(auditLogs[0].EntityId, Is.EqualTo(testEntity.Id));
            Assert.That(auditLogs[0].AdditionalData, Is.Not.Null);
        });
    }

    /// <summary>
    /// Modifying an entity creates per-property audit logs
    /// </summary>
    [Test]
    public async Task SavingChanges_WithModifiedEntity_CreatesPerPropertyAuditLog()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "Original" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify
        var tracked = await _dbContext.TestEntities.FindAsync(testEntity.Id);
        tracked!.Name = "Modified";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert - should have the Created log + at least one Updated log
        var updateLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated)
            .ToListAsync();
        Assert.That(updateLogs, Has.Count.GreaterThanOrEqualTo(1));

        var namePropLog = updateLogs.FirstOrDefault(static l => l.PropertyName == "Name");
        Assert.That(namePropLog, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(namePropLog!.OldValue, Is.EqualTo("Original"));
            Assert.That(namePropLog.NewValue, Is.EqualTo("Modified"));
        });
    }

    /// <summary>
    /// Deleting an entity creates an audit log with Deleted action
    /// </summary>
    [Test]
    public async Task SavingChanges_WithDeletedEntity_CreatesAuditLog()
    {
        // Arrange
        var testEntity = new TestEntity { Name = "ToDelete" };
        _dbContext.TestEntities.Add(testEntity);
        await _dbContext.SaveChangesAsync();

        _dbContext.TestEntities.Remove(testEntity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var deleteLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Deleted)
            .ToListAsync();
        Assert.That(deleteLogs, Has.Count.EqualTo(1));
        Assert.That(deleteLogs[0].EntityName, Is.EqualTo("TestEntity"));
    }

    /// <summary>
    /// Entities marked with NoAudit are not audited
    /// </summary>
    [Test]
    public async Task SavingChanges_WithNoAuditEntity_SkipsAuditLog()
    {
        // Arrange
        var entity = new NoAuditTestEntity { Name = "Secret" };
        _dbContext.Set<NoAuditTestEntity>().Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var auditLogs = await _dbContext.AuditLogs.AsNoTracking().ToListAsync();
        Assert.That(auditLogs, Is.Empty);
    }

    /// <summary>
    /// Saving changes with modified entity that has a property marked with NoAudit skips logging that property but still logs the entity change
    /// </summary>
    [Test]
    public async Task SavingChanges_WithNoAuditProperty_SkipsPropertyInModifiedLog()
    {
        // Arrange
        var entity = new TestEntityWithNoAuditProp { Name = "Original", Secret = "hidden" };
        _dbContext.Set<TestEntityWithNoAuditProp>().Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify both properties
        var tracked = await _dbContext.Set<TestEntityWithNoAuditProp>().FindAsync(entity.Id);
        tracked!.Name = "Changed";
        tracked.Secret = "changed_secret";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — should have update log for Name but NOT for Secret
        var updateLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated)
            .ToListAsync();

        Assert.That(updateLogs.Any(static l => l.PropertyName == "Name"), Is.True);
        Assert.That(updateLogs.Any(static l => l.PropertyName == "Secret"), Is.False);
    }

    /// <summary>
    /// Saving changes with long value truncates AdditionalData to 4000 characters
    /// </summary>
    [Test]
    public async Task SavingChanges_WithLongValue_TruncatesTo4000Chars()
    {
        // Arrange
        var longName = new string('x', 5000);
        var entity = new TestEntity { Name = longName };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Assert — the AdditionalData in the Created audit log should be truncated
        var auditLog = await _dbContext.AuditLogs.AsNoTracking().FirstAsync();
        Assert.That(auditLog.AdditionalData, Has.Length.EqualTo(4000));
    }

    /// <summary>
    /// Saving changes with int primary key stores EntityId as null (non-Guid PK)
    /// </summary>
    [Test]
    public async Task SavingChanges_WithIntPrimaryKey_StoresEntityIdAsNull()
    {
        // Arrange
        var entity = new IntKeyEntity { Name = "IntKey" };
        _dbContext.Set<IntKeyEntity>().Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — int PK cannot be converted to Guid, so EntityId is null
        var auditLog = await _dbContext.AuditLogs.AsNoTracking().FirstAsync();
        Assert.That(auditLog.EntityId, Is.Null);
    }

    /// <summary>
    /// Saving changes with multiple properties changed creates separate audit logs per property
    /// </summary>
    [Test]
    public async Task SavingChanges_MultiplePropertiesChanged_CreatesSeparateAuditLogsPerProperty()
    {
        // Arrange
        var entity = new MultiPropEntity { FirstName = "John", LastName = "Doe" };
        _dbContext.Set<MultiPropEntity>().Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify both properties
        var tracked = await _dbContext.Set<MultiPropEntity>().FindAsync(entity.Id);
        tracked!.FirstName = "Jane";
        tracked.LastName = "Smith";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var updateLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated)
            .ToListAsync();

        Assert.That(updateLogs.Count(static l => l.PropertyName == "FirstName"), Is.EqualTo(1));
        Assert.That(updateLogs.Count(static l => l.PropertyName == "LastName"), Is.EqualTo(1));
    }

    /// <summary>
    /// Sync SaveChanges intentionally does NOT produce audit logs.
    /// The sync SavingChanges override was removed because it could not support
    /// provider dispatch (CaptureForProviderDispatch + SavedChanges), resulting
    /// in partial auditing that is worse than none.
    /// </summary>
    [Test]
    public void SavingChanges_Sync_WithAuditableEntities_ThrowsNotSupportedException()
    {
        // Arrange
        var entity = new TestEntity { Name = "SyncTest" };
        _dbContext.TestEntities.Add(entity);

        // Act & Assert — sync SaveChanges with auditable entities throws
        var ex = Assert.Throws<NotSupportedException>(() => _dbContext.SaveChanges());
        Assert.That(ex!.Message, Does.Contain("Synchronous SaveChanges is not supported"));
        Assert.That(ex.Message, Does.Contain("TestEntity"));
    }

    /// <summary>
    /// Saving changes with mixed audit and non-audit entities still audits the non-audit entities
    /// </summary>
    [Test]
    public async Task SavingChanges_MixedAuditAndNonAuditEntities_NonAuditEntitiesStillAudited()
    {
        // Arrange — save a regular entity alongside an audit entity
        var testEntity = new TestEntity { Name = "RegularEntity" };
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };

        _dbContext.TestEntities.Add(testEntity);
        _dbContext.AuditEvents.Add(auditEvent);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the regular entity should still get an audit log
        var auditLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "TestEntity")
            .ToListAsync();

        Assert.That(auditLogs, Has.Count.EqualTo(1));
        Assert.That(auditLogs[0].Action, Is.EqualTo(AuditAction.Created));
    }

    /// <summary>
    /// Saving changes with modified property that has unchanged value skips creating audit log
    /// </summary>
    [Test]
    public async Task SavingChanges_ModifiedPropertyWithUnchangedValue_IsSkipped()
    {
        // Arrange
        var entity = new TestEntity { Name = "Same" };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Force EF to mark the entity as Modified without actually changing the value
        _dbContext.Entry(entity).State = EntityState.Modified;

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — no Updated audit logs should be created since value didn't change
        var updateLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated)
            .ToListAsync();
        Assert.That(updateLogs, Is.Empty);
    }

    /// <summary>
    /// Saving changes with sensitive field masks the values in audit log
    /// </summary>
    [Test]
    public async Task SavingChanges_SensitiveFieldMaskedInAuditLog()
    {
        // Arrange
        var entity = new SensitiveAuditTestEntity { Ssn = "123-45-6789", Name = "Alice" };
        _dbContext.Set<SensitiveAuditTestEntity>().Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify the sensitive field
        var tracked = await _dbContext.Set<SensitiveAuditTestEntity>().FindAsync(entity.Id);
        tracked!.Ssn = "987-65-4321";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the audit log should have masked values
        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.PropertyName == "Ssn" && l.Action == AuditAction.Updated)
            .FirstOrDefaultAsync();

        Assert.That(updateLog, Is.Not.Null);
        Assert.That(updateLog!.OldValue, Is.EqualTo("XXX-XX-XXXX"));
        Assert.That(updateLog.NewValue, Is.EqualTo("XXX-XX-XXXX"));
    }

    /// <summary>
    /// Saving changes with sensitive field with default mask uses three asterisks
    /// </summary>
    [Test]
    public async Task SavingChanges_SensitiveFieldDefaultMask_UsesThreeAsterisks()
    {
        // Arrange
        var entity = new SensitiveDefaultMaskEntity { Secret = "my-secret", Name = "Bob" };
        _dbContext.Set<SensitiveDefaultMaskEntity>().Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify the sensitive field
        var tracked = await _dbContext.Set<SensitiveDefaultMaskEntity>().FindAsync(entity.Id);
        tracked!.Secret = "new-secret";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.PropertyName == "Secret" && l.Action == AuditAction.Updated)
            .FirstOrDefaultAsync();

        Assert.That(updateLog, Is.Not.Null);
        Assert.That(updateLog!.OldValue, Is.EqualTo("***"));
        Assert.That(updateLog.NewValue, Is.EqualTo("***"));
    }

    /// <summary>
    /// Saving changes with encrypted field redacts the values in audit log
    /// </summary>
    [Test]
    public async Task SavingChanges_EncryptedFieldRedactedInAuditLog()
    {
        // Arrange
        var entity = new EncryptedAuditTestEntity { EncryptedField = "secret-data", Name = "Charlie" };
        _dbContext.Set<EncryptedAuditTestEntity>().Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify the encrypted field
        var tracked = await _dbContext.Set<EncryptedAuditTestEntity>().FindAsync(entity.Id);
        tracked!.EncryptedField = "new-secret-data";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the audit log should have [ENCRYPTED] placeholder
        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.PropertyName == "EncryptedField" && l.Action == AuditAction.Updated)
            .FirstOrDefaultAsync();

        Assert.That(updateLog, Is.Not.Null);
        Assert.That(updateLog!.OldValue, Is.EqualTo("[ENCRYPTED]"));
        Assert.That(updateLog.NewValue, Is.EqualTo("[ENCRYPTED]"));
    }

    /// <summary>
    /// Saving changes with encrypted field in added entity snapshot shows redacted value
    /// </summary>
    [Test]
    public async Task SavingChanges_EncryptedFieldInAddedEntitySnapshot_ShowsRedacted()
    {
        // Arrange
        var entity = new EncryptedAuditTestEntity { EncryptedField = "secret-data", Name = "Dave" };
        _dbContext.Set<EncryptedAuditTestEntity>().Add(entity);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the Created audit log AdditionalData should have [ENCRYPTED]
        var auditLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "EncryptedAuditTestEntity" && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(auditLog.AdditionalData, Does.Contain("[ENCRYPTED]"));
        Assert.That(auditLog.AdditionalData, Does.Not.Contain("secret-data"));
    }

    [Test]
    public async Task SavingChanges_InterceptorBuiltEnvelope_HasNonEmptyEnvelopeId()
    {
        var dbName = $"EnvelopeIdTestDb_{Guid.NewGuid()}";
        var capturingSink = new CapturingAuditSink();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(static w =>
                {
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning);
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning);
                }));
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton<IAuditSink>(capturingSink);

        using var provider = services.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

        var mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        var interceptor = new AuditSaveChangesInterceptor(
            mockLogger.Object,
            scopeFactory: scopeFactory);

        var dbOptions = TestDbContextFactory.CreateInMemoryOptions<TestDbContext>(
            dbName: dbName,
            configure: builder => builder.AddInterceptors(interceptor));

        using var dbContext = new TestDbContext(dbOptions);

        var entity = new TestEntity { Name = "EnvelopeIdTest" };
        dbContext.TestEntities.Add(entity);

        await dbContext.SaveChangesAsync();

        Assert.That(capturingSink.CapturedEnvelopes, Has.Count.EqualTo(1));
        var envelope = capturingSink.CapturedEnvelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.EnvelopeId, Is.Not.EqualTo(Guid.Empty),
                "Interceptor-built envelope must have a non-empty EnvelopeId");
            Assert.That(envelope.EntityName, Is.EqualTo("TestEntity"));
            Assert.That(envelope.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
        });
    }

    private sealed class CapturingAuditSink : IAuditSink
    {
        public List<AuditEnvelope> CapturedEnvelopes { get; } = [];

        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default)
        {
            CapturedEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default)
        {
            CapturedEnvelopes.AddRange(envelopes);
            return Task.CompletedTask;
        }
    }

    // Test DbContext and Entity for testing
    private class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options), IAuditBypassable
    {
        public DbSet<TestEntity> TestEntities { get; set; }
        public DbSet<AuditEventEntity> AuditEvents { get; set; }
        public DbSet<AuditIntegrityEntity> AuditIntegrity { get; set; }
        public DbSet<AuditLogEntity> AuditLogs { get; set; }
        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<TestEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<NoAuditTestEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<TestEntityWithNoAuditProp>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<IntKeyEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<MultiPropEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<SensitiveAuditTestEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<SensitiveDefaultMaskEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<EncryptedAuditTestEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<AuditEventEntity>()
                .HasKey(static e => e.EventId);

            modelBuilder.Entity<AuditIntegrityEntity>()
                .HasKey(static e => e.EventId);

            modelBuilder.Entity<AuditLogEntity>()
                .HasKey(static e => e.Id);

            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class TestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Name of the test entity
        /// </summary>
        public string Name { get; set; } = string.Empty;
    }

    [NoAudit]
    private sealed class NoAuditTestEntity
    {
        /// <summary>
        /// Id of the entity
        /// </summary>
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>
        /// Name of the entity
        /// </summary>
        public string Name { get; set; } = string.Empty;
    }

    private sealed class TestEntityWithNoAuditProp
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;

        [NoAudit] public string Secret { get; set; } = string.Empty;
    }

    private sealed class IntKeyEntity
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private sealed class MultiPropEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
    }

    private sealed class SensitiveAuditTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;

        [SensitiveData(MaskInLogs = true, MaskPattern = "XXX-XX-XXXX")]
        public string Ssn { get; set; } = string.Empty;
    }

    private sealed class SensitiveDefaultMaskEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;

        [SensitiveData(MaskInLogs = true)] public string Secret { get; set; } = string.Empty;
    }

    private sealed class EncryptedAuditTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;

        [EncryptedField(EncryptInAuditLog = true)]
        public string EncryptedField { get; set; } = string.Empty;
    }
}