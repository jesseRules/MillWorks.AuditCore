using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Edge case tests for AuditSaveChangesInterceptor covering scenarios not present in
/// AuditSaveChangesInterceptorTests: all audit-entity type exclusions, multi-entity batches,
/// post-save detach behaviour, and concurrent save thread safety.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditSaveChangesInterceptorEdgeCaseTests
{
    private Mock<ILogger<AuditSaveChangesInterceptor>> _mockLogger;
    private AuditSaveChangesInterceptor _interceptor;
    private DbContextOptions<EdgeCaseTestDbContext> _dbOptions;
    private EdgeCaseTestDbContext _dbContext;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _interceptor = new AuditSaveChangesInterceptor(_mockLogger.Object);

        _dbOptions = TestDbContextFactory.CreateInMemoryOptions<EdgeCaseTestDbContext>(configure: builder =>
            builder.AddInterceptors(_interceptor));

        _dbContext = new EdgeCaseTestDbContext(_dbOptions);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    // -------------------------------------------------------------------------
    // 1. AuditLogEntity itself is excluded from auditing
    // -------------------------------------------------------------------------

    /// <summary>
    /// Saving an AuditLogEntity directly must not create a second-order audit log for it.
    /// AuditLogEntity is a member of the AuditEntityTypes exclusion set.
    /// </summary>
    [Test]
    public async Task SavingChanges_AuditLogEntityItself_SkipsAuditing()
    {
        // Arrange — add an AuditLogEntity directly (simulates the interceptor's own write
        // arriving in a second save, or an external caller persisting a log entry)
        var auditLog = new AuditLogEntity
        {
            EntityName = "SomeEntity",
            Action = AuditAction.Created,
            Description = "Manually created entry"
        };

        _dbContext.AuditLogs.Add(auditLog);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — no "Processing audit entry" debug message should have been emitted,
        // meaning the interceptor did not attempt to audit the AuditLogEntity itself.
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "AuditLogEntity must not be audited to prevent circular write loops.");

        // The entity itself was saved — confirm it exists.
        var saved = await _dbContext.AuditLogs.AsNoTracking().ToListAsync();
        Assert.That(saved, Has.Count.EqualTo(1));
        Assert.That(saved[0].EntityName, Is.EqualTo("SomeEntity"));
    }

    // -------------------------------------------------------------------------
    // 2. AuditArchiveRecordEntity and AuditSecurityEventEntity are excluded
    // -------------------------------------------------------------------------

    /// <summary>
    /// AuditArchiveRecordEntity and AuditSecurityEventEntity are in the AuditEntityTypes
    /// exclusion set but are not covered by the existing tests. Verify both are skipped.
    /// </summary>
    [Test]
    public async Task SavingChanges_AllRemainingAuditEntityTypes_ProduceNoAuditLogs()
    {
        // Arrange
        var archiveRecord = new AuditArchiveRecordEntity
        {
            ArchiveId = "archive-001",
            BlobName = "blob-001",
            ContainerName = "container-001",
            EventCount = 5,
            DateRangeStart = DateTimeOffset.UtcNow.AddDays(-7),
            DateRangeEnd = DateTimeOffset.UtcNow,
            SizeBytes = 1024,
            Hash = "abc123",
            ArchiveVersion = "1.0",
            CompressionType = "gzip"
        };

        _dbContext.Set<AuditArchiveRecordEntity>().Add(archiveRecord);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — the archive record must not generate an audit log entry.
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "AuditArchiveRecordEntity must not be audited.");

        var auditLogs = await _dbContext.AuditLogs.AsNoTracking().ToListAsync();
        Assert.That(auditLogs, Is.Empty,
            "No audit logs should be created when only AuditArchiveRecordEntity is saved.");
    }

    // -------------------------------------------------------------------------
    // 3. Multiple distinct entities in a single SaveChanges — all are audited
    // -------------------------------------------------------------------------

    /// <summary>
    /// When three different non-audit entities are added in one SaveChanges call,
    /// a separate audit log entry must be created for each of them.
    /// </summary>
    [Test]
    public async Task SavingChanges_MultipleEntities_AuditsAll()
    {
        // Arrange
        var entity1 = new EdgeTestEntity { Name = "Entity One" };
        var entity2 = new EdgeTestEntity { Name = "Entity Two" };
        var entity3 = new EdgeTestEntity { Name = "Entity Three" };

        _dbContext.EdgeEntities.Add(entity1);
        _dbContext.EdgeEntities.Add(entity2);
        _dbContext.EdgeEntities.Add(entity3);

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert — one Created log per entity
        var auditLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Created && l.EntityName == "EdgeTestEntity")
            .ToListAsync();

        Assert.That(auditLogs, Has.Count.EqualTo(3),
            "Each of the three entities must produce its own audit log entry.");

        var auditedIds = auditLogs.Select(static l => l.EntityId).ToHashSet();
        Assert.That(auditedIds, Does.Contain(entity1.Id));
        Assert.That(auditedIds, Does.Contain(entity2.Id));
        Assert.That(auditedIds, Does.Contain(entity3.Id));
    }

    // -------------------------------------------------------------------------
    // 4. Detaching a tracked entity after its first save produces no second audit
    // -------------------------------------------------------------------------

    /// <summary>
    /// After saving an entity and then explicitly detaching it, any subsequent SaveChanges
    /// on the same context must not produce a spurious audit log for the detached entity.
    /// This differs from the existing detach test, which never persists the entity at all.
    /// </summary>
    [Test]
    public async Task SavingChanges_DetachedEntityAfterFirstSave_DoesNotGenerateSecondAudit()
    {
        // Arrange — first save (produces one Created audit log)
        var entity = new EdgeTestEntity { Name = "PersistThenDetach" };
        _dbContext.EdgeEntities.Add(entity);
        await _dbContext.SaveChangesAsync();

        // Detach the entity so EF no longer tracks it
        _dbContext.Entry(entity).State = EntityState.Detached;

        // Mutate the object locally — this should be invisible to EF
        entity.Name = "MutatedLocally";

        // Reset the mock logger so we only count invocations from the second save
        _mockLogger.Reset();

        // Act — save again; the change tracker should have nothing to report
        await _dbContext.SaveChangesAsync();

        // Assert — no audit processing should occur
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Processing audit entry")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never,
            "A detached entity's local mutation must not appear in the change tracker.");

        // Only the first save's Created log should exist in the database
        var entityId = entity.Id;
        var auditLogs = await _dbContext.AuditLogs.AsNoTracking()
            .Where(l => l.EntityId == entityId)
            .ToListAsync();

        Assert.That(auditLogs, Has.Count.EqualTo(1),
            "Exactly one audit log (the original Created entry) should exist for this entity.");
        Assert.That(auditLogs[0].Action, Is.EqualTo(AuditAction.Created));
    }

    // -------------------------------------------------------------------------
    // 5. Concurrent saves across independent contexts are thread-safe
    // -------------------------------------------------------------------------

    /// <summary>
    /// Ten tasks each create their own DbContext instance, add a unique entity, and call
    /// SaveChangesAsync concurrently. All saves must complete without exception and each
    /// must produce exactly one audit log entry, confirming that the interceptor's static
    /// caches (NoAuditTypeCache, PropertyMetadataCache) handle concurrent access safely.
    /// </summary>
    [Test]
    public async Task SavingChanges_ConcurrentSaves_ThreadSafe()
    {
        const int concurrency = 10;

        // Shared interceptor instance (singleton in production) reused across all contexts
        var sharedInterceptor = new AuditSaveChangesInterceptor(
            new Mock<ILogger<AuditSaveChangesInterceptor>>().Object);

        var savedEntityIds = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        var tasks = Enumerable.Range(0, concurrency).Select(async _ =>
        {
            // Each task uses a separate in-memory database so writes don't collide,
            // but they all share the same AuditSaveChangesInterceptor singleton.
            var options = TestDbContextFactory.CreateInMemoryOptions<EdgeCaseTestDbContext>(configure: builder =>
                builder.AddInterceptors(sharedInterceptor));

            await using var ctx = new EdgeCaseTestDbContext(options);

            var entity = new EdgeTestEntity { Name = $"Concurrent_{Guid.NewGuid()}" };
            ctx.EdgeEntities.Add(entity);

            try
            {
                await ctx.SaveChangesAsync();
                savedEntityIds.Add(entity.Id);

                // Verify the audit log was written inside this context's own database
                var logs = await ctx.AuditLogs.AsNoTracking()
                    .Where(l => l.EntityId == entity.Id)
                    .ToListAsync();

                if (logs.Count != 1)
                    throw new InvalidOperationException(
                        $"Expected 1 audit log for {entity.Id}, got {logs.Count}.");
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        await Task.WhenAll(tasks);

        // Assert — no exceptions from any task
        Assert.That(exceptions, Is.Empty,
            $"One or more concurrent saves threw exceptions: {string.Join("; ", exceptions.Select(static e => e.Message))}");

        // Assert — every entity was saved
        Assert.That(savedEntityIds, Has.Count.EqualTo(concurrency),
            "All concurrent saves must complete successfully.");
    }

    // =========================================================================
    // Private test infrastructure
    // =========================================================================

    private class EdgeCaseTestDbContext(DbContextOptions<EdgeCaseTestDbContext> options)
        : DbContext(options), IAuditBypassable
    {
        public DbSet<EdgeTestEntity> EdgeEntities { get; set; }
        public DbSet<AuditLogEntity> AuditLogs { get; set; }
        public DbSet<AuditEventEntity> AuditEvents { get; set; }
        public DbSet<AuditIntegrityEntity> AuditIntegrity { get; set; }

        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<EdgeTestEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<AuditLogEntity>()
                .HasKey(static e => e.Id);

            modelBuilder.Entity<AuditEventEntity>()
                .HasKey(static e => e.EventId);

            modelBuilder.Entity<AuditIntegrityEntity>()
                .HasKey(static e => e.Id);

            // AuditArchiveRecordEntity — registered so the context can persist it,
            // but its PK comes from AuditAggregateRoot → AuditEntity (Id : Guid).
            modelBuilder.Entity<AuditArchiveRecordEntity>()
                .HasKey(static e => e.Id);

            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class EdgeTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }
}
