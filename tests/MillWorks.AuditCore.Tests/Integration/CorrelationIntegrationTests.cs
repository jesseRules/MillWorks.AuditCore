using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for CorrelationId correlation between AuditEvents and AuditLogs tables.
/// Uses SQLite in-memory database with the interceptor wired up.
/// </summary>
[TestFixture]
[Category("Integration")]
public class CorrelationIntegrationTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<CorrelationSqliteDbContext> _options = null!;
    private AuditSaveChangesInterceptor _interceptor = null!;
    private ServiceProvider _provider = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseSqlite(_connection)
                .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddScoped<IAuditEntityBatchWriter, AuditEntityBatchWriter>();
        services.AddScoped<IAuditEventBatchWriter, AuditEventBatchWriter>();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditSink, ImmediateSink>();
        _provider = services.BuildServiceProvider();

        _interceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            scopeFactory: _provider.GetRequiredService<IServiceScopeFactory>());

        _options = new DbContextOptionsBuilder<CorrelationSqliteDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .AddInterceptors(_interceptor)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void CleanupData()
    {
        using var context = CreateContext();
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditEvents\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditLogs\"");
    }

    /// <summary>
    /// Test 0.6e: Full pipeline — interceptor stamps CorrelationId and both tables
    /// can be queried with the same value.
    /// Note: The two paths are saved separately because AuditDbContext
    /// sets BypassAuditInterceptor=true whenever audit entities are in the tracker,
    /// which would suppress interceptor processing for the TestEntity.
    /// </summary>
    [Test]
    public async Task InterceptorStampsCorrelationId_MatchesManualAuditEvent()
    {
        using var context = CreateContext();
        var correlationId = "integration-correlation-456";

        // Simulate middleware setting correlation
        context.CurrentCorrelationId = correlationId;

        // Path 1: Entity change triggers interceptor → AuditLogEntity with CorrelationId
        context.TestEntities.Add(new TestEntity { Name = "CorrelationTest" });
        await context.SaveChangesAsync();

        // Path 2: Seed an explicit AuditEvent with the same correlation ID
        // (In production, AuditLogger.LogAsync does this via InternalAuditEventRepository)
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Integration.Test",
            CorrelationId = correlationId,
            User = "tester@test.com",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        // Assert: AuditEvent has the correlation ID
        var auditEvent = await context.AuditEvents.AsNoTracking().FirstAsync();
        Assert.That(auditEvent.CorrelationId, Is.EqualTo(correlationId));

        // Assert: AuditLog has the same correlation ID
        var auditLog = await context.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == "TestEntity")
            .FirstAsync();
        Assert.That(auditLog.CorrelationId, Is.EqualTo(correlationId));
    }

    /// <summary>
    /// Test 0.6f: LINQ join across AuditEvents and AuditLogs on CorrelationId works.
    /// </summary>
    [Test]
    public async Task JoinQuery_AuditEvents_To_AuditLogs_OnCorrelationId()
    {
        using var context = CreateContext();
        var correlationId = "join-test-789";

        // Seed AuditEvent
        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "User.Updated",
            CorrelationId = correlationId,
            User = "tester@test.com",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Seed AuditLogs with same correlation
        context.AuditLogs.Add(new AuditLogEntity
        {
            EntityName = "User",
            EntityId = Guid.NewGuid(),
            Action = AuditAction.Updated,
            PropertyName = "Email",
            OldValue = "old@test.com",
            NewValue = "new@test.com",
            CorrelationId = correlationId
        });
        context.AuditLogs.Add(new AuditLogEntity
        {
            EntityName = "User",
            EntityId = Guid.NewGuid(),
            Action = AuditAction.Updated,
            PropertyName = "Name",
            OldValue = "Old Name",
            NewValue = "New Name",
            CorrelationId = correlationId
        });

        await context.SaveChangesAsync();

        // Act — join query
        var joined = await (
            from e in context.AuditEvents.AsNoTracking()
            join l in context.AuditLogs.AsNoTracking()
                on e.CorrelationId equals l.CorrelationId
            where e.CorrelationId == correlationId
            select new { Event = e.EventType, Log = l.PropertyName }
        ).ToListAsync();

        // Assert
        Assert.That(joined, Has.Count.EqualTo(2));
        Assert.That(joined.Select(static j => j.Log), Is.EquivalentTo(new[] { "Email", "Name" }));
        Assert.That(joined.All(static j => j.Event == "User.Updated"));
    }

    private CorrelationSqliteDbContext CreateContext() => new(_options);

    public void Dispose()
    {
        _provider?.Dispose();
        _connection?.Close();
        _connection?.Dispose();
    }

    /// <summary>
    /// SQLite test context that inherits AuditDbContext and adds TestEntities.
    /// </summary>
    private sealed class CorrelationSqliteDbContext(DbContextOptions<CorrelationSqliteDbContext> options)
        : AuditDbContext(options)
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
