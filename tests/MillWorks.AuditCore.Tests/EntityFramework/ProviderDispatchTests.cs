using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Providers.Base;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests for the interceptor's provider dispatch mechanism (Phase 1).
/// </summary>
[TestFixture]
public class ProviderDispatchTests
{
    private AuditSaveChangesInterceptor _interceptor = null!;
    private ProviderDispatchTestDbContext _dbContext = null!;
    private Mock<IAuditProviderDispatcher> _mockDispatcher = null!;
    private AuditProviderTypeMap _typeMap = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void Setup()
    {
        var mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _interceptor = new AuditSaveChangesInterceptor(mockLogger.Object);

        _typeMap = new AuditProviderTypeMap();
        _mockDispatcher = new Mock<IAuditProviderDispatcher>();

        var services = new ServiceCollection();
        services.AddSingleton(_typeMap);
        services.AddSingleton(_mockDispatcher.Object);
        _serviceProvider = services.BuildServiceProvider();

        var options = TestDbContextFactory.CreateInMemoryOptions<ProviderDispatchTestDbContext>(
            configure: builder => builder.AddInterceptors(_interceptor));

        _dbContext = new ProviderDispatchTestDbContext(options);
        _dbContext.ScopedServiceProvider = _serviceProvider;
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    [Test]
    public async Task SaveChanges_WithRegisteredProvider_DispatchesProvider()
    {
        // Arrange: register a provider for "TestEntity"
        _typeMap.Register("TestEntity", typeof(TestAuditProvider));

        _dbContext.TestEntities.Add(new TestEntity { Name = "Dispatched" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: dispatcher was called with one pending dispatch
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.Is<IReadOnlyList<PendingProviderDispatch>>(list =>
                list.Count == 1 &&
                list[0].EntityTypeName == "TestEntity" &&
                list[0].Action == "Created"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SaveChanges_WithoutRegisteredProvider_NoDispatch()
    {
        // Arrange: no providers registered
        _dbContext.TestEntities.Add(new TestEntity { Name = "NoProvider" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: dispatcher was never called
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<IReadOnlyList<PendingProviderDispatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SaveChanges_ModifiedEntity_CapturesOldValues()
    {
        // Arrange: register provider and create initial entity
        _typeMap.Register("TestEntity", typeof(TestAuditProvider));
        var entity = new TestEntity { Name = "Original" };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _mockDispatcher.Invocations.Clear();

        // Modify
        var tracked = await _dbContext.TestEntities.FindAsync(entity.Id);
        tracked!.Name = "Modified";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: old values captured
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.Is<IReadOnlyList<PendingProviderDispatch>>(list =>
                list.Count == 1 &&
                list[0].Action == "Updated" &&
                list[0].OldValues != null),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SaveChanges_ReEntrancyGuard_PreventsRecursion()
    {
        // Arrange: register provider, simulate re-entrancy by setting guard
        _typeMap.Register("TestEntity", typeof(TestAuditProvider));
        _dbContext.IsDispatchingProviders = true;

        _dbContext.TestEntities.Add(new TestEntity { Name = "Reentrant" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: dispatcher never called due to re-entrancy guard
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<IReadOnlyList<PendingProviderDispatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);

        _dbContext.IsDispatchingProviders = false; // Reset for cleanup
    }

    [Test]
    public async Task SaveChanges_NoScopedServiceProvider_SkipsDispatch()
    {
        // Arrange: provider registered but no ScopedServiceProvider set
        _typeMap.Register("TestEntity", typeof(TestAuditProvider));
        _dbContext.ScopedServiceProvider = null;

        _dbContext.TestEntities.Add(new TestEntity { Name = "NoProvider" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: dispatcher never called
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<IReadOnlyList<PendingProviderDispatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SaveChanges_AuditEntities_NotCapturedForDispatch()
    {
        // Arrange: register a provider for "AuditEventEntity" (should be filtered out)
        _typeMap.Register("AuditEventEntity", typeof(TestAuditProvider));

        _dbContext.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert: dispatcher never called — audit entities are excluded
        _mockDispatcher.Verify(d => d.DispatchAsync(
            It.IsAny<IReadOnlyList<PendingProviderDispatch>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task SaveChangesFailed_ClearsPendingProviderDispatches()
    {
        // Arrange: use a second interceptor that throws in SavedChangesAsync to trigger
        // EF's SaveChangesFailed pipeline. Our AuditSaveChangesInterceptor's
        // SaveChangesFailedAsync override should clear PendingProviderDispatches.
        var failInterceptor = new FailOnSecondSaveInterceptor();
        var options = TestDbContextFactory.CreateInMemoryOptions<ProviderDispatchTestDbContext>(
            configure: builder => builder
                .AddInterceptors(_interceptor)
                .AddInterceptors(failInterceptor));

        await using var ctx = new ProviderDispatchTestDbContext(options);
        ctx.ScopedServiceProvider = _serviceProvider;

        // First save succeeds (primes the context)
        ctx.TestEntities.Add(new TestEntity { Name = "setup" });
        await ctx.SaveChangesAsync();

        // Set stale dispatches as if CaptureForProviderDispatch had run
        ctx.PendingProviderDispatches =
        [
            new PendingProviderDispatch("TestEntity", "Created", new TestEntity { Name = "Stale" }, null)
        ];
        Assert.That(ctx.PendingProviderDispatches, Is.Not.Null);

        // Arm the failing interceptor — next save will throw in SavedChangesAsync,
        // which triggers SaveChangesFailedAsync on all interceptors
        failInterceptor.ShouldFail = true;
        ctx.TestEntities.Add(new TestEntity { Name = "will_fail" });

        // Act
        try { await ctx.SaveChangesAsync(); }
        catch (InvalidOperationException) { /* expected */ }

        // Assert: our interceptor's SaveChangesFailedAsync cleared the stale dispatches
        Assert.That(ctx.PendingProviderDispatches, Is.Null,
            "SaveChangesFailedAsync must clear PendingProviderDispatches to prevent stale dispatch on next save.");
    }

    /// <summary>
    /// Interceptor that throws in SavedChangesAsync when armed, triggering the SaveChangesFailed pipeline.
    /// </summary>
    private sealed class FailOnSecondSaveInterceptor : SaveChangesInterceptor
    {
        public bool ShouldFail { get; set; }

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
                throw new InvalidOperationException("Simulated post-save failure");
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ProviderDispatchTestDbContext(DbContextOptions<ProviderDispatchTestDbContext> options)
        : AuditApplicationDbContext(options)
    {
        public DbSet<TestEntity> TestEntities { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<TestEntity>().HasKey(static e => e.Id);
        }
    }

    private sealed class TestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    // Stub provider type — only used for type registration, never actually resolved in these tests
    private sealed class TestAuditProvider : IAuditProvider
    {
        public string EntityType => "TestEntity";
        public Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null)
            => Task.FromResult(new AuditEvent { EventType = $"TestEntity.{action}" });
        public Task<bool> ShouldAuditAsync(string action, object entity) => Task.FromResult(true);
        public Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity) => Task.CompletedTask;
        public Dictionary<string, object?> GetChanges(object? oldValues, object? newValues) => new();
    }
}
