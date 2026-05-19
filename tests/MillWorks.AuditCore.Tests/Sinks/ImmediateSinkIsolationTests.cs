using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.EntityFramework.Sinks;

namespace MillWorks.AuditCore.Tests.Sinks;

/// <summary>
/// Phase 05 acceptance: <see cref="ImmediateSink"/> persists audit rows on a
/// fresh scoped <see cref="AuditDbContext"/> resolved from
/// <see cref="IServiceScopeFactory"/>, decoupling the audit write from any
/// consumer transaction in flight.
/// <para>
/// The audit DbContext and the consumer DbContext intentionally use distinct
/// SQLite connections so the connection-level isolation is observable: a
/// rollback on the consumer connection cannot reach an already-committed
/// audit row on the audit connection.
/// </para>
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class ImmediateSinkIsolationTests
{
    private SqliteConnection _auditConnection = null!;
    private SqliteConnection _consumerConnection = null!;

    [SetUp]
    public void SetUp()
    {
        _auditConnection = new SqliteConnection("DataSource=:memory:");
        _auditConnection.Open();

        _consumerConnection = new SqliteConnection("DataSource=:memory:");
        _consumerConnection.Open();
    }

    [TearDown]
    public void TearDown()
    {
        _auditConnection?.Dispose();
        _consumerConnection?.Dispose();
    }

    [Test]
    public async Task SuccessPath_AuditRowSurvivesConsumerRollback()
    {
        using var provider = BuildAuditProviderWithDefaultSink();
        EnsureAuditSchema(provider);

        var interceptor = new AuditSaveChangesInterceptor(
            logger: NullLogger<AuditSaveChangesInterceptor>.Instance,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var consumerOptions = BuildConsumerOptions(interceptor);

        await using (var seedCtx = new ConsumerStubContext(consumerOptions))
        {
            await seedCtx.Database.EnsureCreatedAsync();
        }

        await using (var consumerCtx = new ConsumerStubContext(consumerOptions))
        await using (var tx = await consumerCtx.Database.BeginTransactionAsync())
        {
            consumerCtx.StubItems.Add(new StubItem { Name = "rolled-back-by-consumer" });
            await consumerCtx.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        await using (var verifyConsumer = new ConsumerStubContext(consumerOptions))
        {
            Assert.That(await verifyConsumer.StubItems.CountAsync(), Is.Zero,
                "Consumer rollback must drop the stub item on the consumer connection.");
        }

        using (var auditScope = provider.CreateScope())
        {
            var auditCtx = auditScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var auditRows = await auditCtx.Set<AuditLogEntity>()
                .Where(r => r.EntityName == nameof(StubItem))
                .ToListAsync();

            Assert.That(auditRows, Has.Count.EqualTo(1),
                "Audit row must survive consumer rollback because it commits on the separate audit connection.");
            Assert.That(auditRows[0].Action, Is.EqualTo(AuditAction.Created));
        }
    }

    [Test]
    public async Task PermissiveFailure_SinkThrows_DoesNotRollBackConsumerSave()
    {
        var diagnostics = new AuditDiagnostics();

        using var provider = BuildAuditProviderWithSink(new ThrowingSink());
        EnsureAuditSchema(provider);

        var interceptor = new AuditSaveChangesInterceptor(
            logger: NullLogger<AuditSaveChangesInterceptor>.Instance,
            diagnostics: diagnostics,
            failureMode: AuditFailureMode.Permissive,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var consumerOptions = BuildConsumerOptions(interceptor);

        await using (var seedCtx = new ConsumerStubContext(consumerOptions))
        {
            await seedCtx.Database.EnsureCreatedAsync();
        }

        await using (var consumerCtx = new ConsumerStubContext(consumerOptions))
        {
            consumerCtx.StubItems.Add(new StubItem { Name = "permissive-survives-sink-throw" });
            await consumerCtx.SaveChangesAsync();
        }

        await using (var verifyConsumer = new ConsumerStubContext(consumerOptions))
        {
            Assert.That(await verifyConsumer.StubItems.CountAsync(), Is.EqualTo(1),
                "Permissive mode must let the consumer save commit even when the sink throws.");
        }

        Assert.That(diagnostics.InterceptorAuditFailureCount, Is.EqualTo(1),
            "Sink-publish failure under permissive mode must increment the interceptor failure counter.");

        using (var auditScope = provider.CreateScope())
        {
            var auditCtx = auditScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            Assert.That(await auditCtx.Set<AuditLogEntity>().CountAsync(), Is.Zero,
                "Throwing sink never reached the writer, so no audit row should be present.");
        }
    }

    private ServiceProvider BuildAuditProviderWithDefaultSink()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_auditConnection));
        services.AddScoped<IAuditEntityWriter, AuditDbContextEntityWriter>();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditSink, ImmediateSink>();
        return services.BuildServiceProvider();
    }

    private ServiceProvider BuildAuditProviderWithSink(IAuditSink sink)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_auditConnection));
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton(sink);
        return services.BuildServiceProvider();
    }

    private static void EnsureAuditSchema(ServiceProvider provider)
    {
        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        ctx.Database.EnsureCreated();
    }

    private DbContextOptions<ConsumerStubContext> BuildConsumerOptions(AuditSaveChangesInterceptor interceptor)
    {
        return new DbContextOptionsBuilder<ConsumerStubContext>()
            .UseSqlite(_consumerConnection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .AddInterceptors(interceptor)
            .Options;
    }

    private sealed class ConsumerStubContext : AuditDbContext
    {
        public ConsumerStubContext(DbContextOptions<ConsumerStubContext> options) : base(options)
        {
        }

        public DbSet<StubItem> StubItems { get; set; } = null!;
    }

    private sealed class StubItem
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class ThrowingSink : IAuditSink
    {
        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("test-induced sink-publish failure");

        public Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("test-induced sink-publish failure");
    }
}
