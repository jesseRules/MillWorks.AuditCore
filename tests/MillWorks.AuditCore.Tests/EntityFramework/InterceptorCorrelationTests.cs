using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests that the interceptor stamps CorrelationId on AuditLogEntity records
/// when CurrentCorrelationId is set on the AuditApplicationDbContext.
/// </summary>
[TestFixture]
public class InterceptorCorrelationTests
{
    private AuditSaveChangesInterceptor _interceptor = null!;
    private CorrelationTestDbContext _dbContext = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void Setup()
    {
        var dbName = $"CorrelationTestDb_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditApplicationDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
                .ConfigureWarnings(static w =>
                {
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning);
                    w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.ManyServiceProvidersCreatedWarning);
                }));
        services.AddScoped<IAuditEntityWriter, AuditDbContextEntityWriter>();
        services.AddScoped<IAuditSink, ImmediateSink>();

        _provider = services.BuildServiceProvider();
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        var mockLogger = new Mock<ILogger<AuditSaveChangesInterceptor>>();
        _interceptor = new AuditSaveChangesInterceptor(
            mockLogger.Object,
            scopeFactory: scopeFactory);

        var options = TestDbContextFactory.CreateInMemoryOptions<CorrelationTestDbContext>(
            dbName: dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        _dbContext = new CorrelationTestDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task SavingChanges_WithCorrelationId_StampsItOnAuditLog()
    {
        // Arrange
        _dbContext.CurrentCorrelationId = "test-correlation-123";
        _dbContext.TestEntities.Add(new TestEntity { Name = "Correlated" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var log = await _dbContext.AuditLogs.AsNoTracking().FirstAsync();
        Assert.That(log.CorrelationId, Is.EqualTo("test-correlation-123"));
    }

    [Test]
    public async Task SavingChanges_WithoutCorrelationId_LeavesItNull()
    {
        // Arrange — no correlation set
        _dbContext.TestEntities.Add(new TestEntity { Name = "NoCorrelation" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var log = await _dbContext.AuditLogs.AsNoTracking().FirstAsync();
        Assert.That(log.CorrelationId, Is.Null);
    }

    [Test]
    public async Task SavingChanges_MultipleEntities_AllShareCorrelationId()
    {
        // Arrange
        _dbContext.CurrentCorrelationId = "batch-correlation";
        _dbContext.TestEntities.Add(new TestEntity { Name = "One" });
        _dbContext.TestEntities.Add(new TestEntity { Name = "Two" });
        _dbContext.TestEntities.Add(new TestEntity { Name = "Three" });

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var logs = await _dbContext.AuditLogs.AsNoTracking().ToListAsync();
        Assert.That(logs, Has.Count.EqualTo(3));
        Assert.That(logs.Select(static l => l.CorrelationId).Distinct(),
            Is.EqualTo(new[] { "batch-correlation" }));
    }

    [Test]
    public async Task SavingChanges_ModifiedEntity_AuditLogHasCorrelationId()
    {
        // Initial save (no correlation)
        var entity = new TestEntity { Name = "Original" };
        _dbContext.TestEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();

        // Modify with correlation
        _dbContext.CurrentCorrelationId = "update-correlation";
        var tracked = await _dbContext.TestEntities.FindAsync(entity.Id);
        tracked!.Name = "Modified";

        // Act
        await _dbContext.SaveChangesAsync();

        // Assert
        var updateLog = await _dbContext.AuditLogs.AsNoTracking()
            .Where(static l => l.Action == AuditAction.Updated)
            .FirstAsync();
        Assert.That(updateLog.CorrelationId, Is.EqualTo("update-correlation"));
    }

    // Test context that extends AuditApplicationDbContext to expose CurrentCorrelationId
    private sealed class CorrelationTestDbContext(DbContextOptions<CorrelationTestDbContext> options)
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
}
