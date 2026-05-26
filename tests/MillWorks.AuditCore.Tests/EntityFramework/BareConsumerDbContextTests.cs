using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Tests that verify the interceptor works with consumer DbContexts that do NOT
/// include any AuditCore entities (AuditLogEntity, AuditEventEntity, etc.).
/// This validates Phase 07's removal of the AuditLogEntity coupling in GetAuditableEntries.
/// </summary>
[TestFixture]
public class BareConsumerDbContextTests
{
    private ServiceProvider _provider = null!;
    private AuditSaveChangesInterceptor _interceptor = null!;
    private string _dbName = null!;

    [SetUp]
    public void Setup()
    {
        _dbName = $"TestDb_{Guid.NewGuid()}";

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o =>
            o.UseInMemoryDatabase(_dbName)
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

        _interceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            scopeFactory: scopeFactory);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    [Test]
    public async Task BareConsumerDbContext_WithoutAuditEntities_AuditRowsLand()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions<BareConsumerDbContext>(
            dbName: _dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        await using var consumerCtx = new BareConsumerDbContext(options);
        consumerCtx.Products.Add(new Product { Name = "Widget" });
        await consumerCtx.SaveChangesAsync();

        await using var scope = _provider.CreateAsyncScope();
        var auditCtx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var auditLog = await auditCtx.AuditLogs.SingleOrDefaultAsync();

        Assert.That(auditLog, Is.Not.Null, "Audit row should exist");
        Assert.That(auditLog!.EntityName, Is.EqualTo("Product"));
        Assert.That(auditLog.Action, Is.EqualTo(AuditAction.Created));
    }

    [Test]
    public async Task BareConsumerDbContext_WithIAuditContextSource_UserAndCorrelationFlowToEnvelope()
    {
        var recordingSink = new RecordingSink();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton<IAuditSink>(recordingSink);
        var provider = services.BuildServiceProvider();

        var interceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var options = TestDbContextFactory.CreateInMemoryOptions<BareConsumerDbContextWithContextSource>(
            dbName: $"ContextSource_{Guid.NewGuid()}",
            configure: builder => builder.AddInterceptors(interceptor));

        await using var consumerCtx = new BareConsumerDbContextWithContextSource(options)
        {
            CurrentUserId = "user-123",
            CurrentCorrelationId = "corr-456",
            CurrentIpAddress = "10.0.0.1",
            CurrentUserAgent = "TestAgent/1.0"
        };

        consumerCtx.Products.Add(new Product { Name = "Gadget" });
        await consumerCtx.SaveChangesAsync();

        Assert.That(recordingSink.Envelopes, Has.Count.EqualTo(1));
        var envelope = recordingSink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.UserId, Is.EqualTo("user-123"));
            Assert.That(envelope.CorrelationId, Is.EqualTo("corr-456"));
            Assert.That(envelope.IpAddress, Is.EqualTo("10.0.0.1"));
            Assert.That(envelope.UserAgent, Is.EqualTo("TestAgent/1.0"));
        });

        provider.Dispose();
    }

    [Test]
    public async Task BareConsumerDbContext_WithIAuditProviderDispatchSource_ProvidersDispatch()
    {
        var dispatched = new List<PendingProviderDispatch>();
        var mockDispatcher = new Mock<IAuditProviderDispatcher>();
        mockDispatcher
            .Setup(d => d.DispatchAsync(It.IsAny<IReadOnlyList<PendingProviderDispatch>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<PendingProviderDispatch>, CancellationToken>((d, _) => dispatched.AddRange(d))
            .Returns(Task.CompletedTask);

        var providerMap = new AuditProviderTypeMap();
        providerMap.Register("Product", typeof(ITestProductAuditProvider));

        var consumerServices = new ServiceCollection();
        consumerServices.AddSingleton(providerMap);
        consumerServices.AddSingleton(mockDispatcher.Object);
        var consumerProvider = consumerServices.BuildServiceProvider();

        var options = TestDbContextFactory.CreateInMemoryOptions<BareConsumerDbContextWithDispatchSource>(
            dbName: _dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        await using var consumerCtx = new BareConsumerDbContextWithDispatchSource(options)
        {
            ScopedServiceProvider = consumerProvider
        };

        consumerCtx.Products.Add(new Product { Name = "Dispatchable" });
        await consumerCtx.SaveChangesAsync();

        Assert.That(dispatched, Has.Count.EqualTo(1));
        Assert.That(dispatched[0].EntityTypeName, Is.EqualTo("Product"));
        Assert.That(dispatched[0].Action, Is.EqualTo("Created"));
    }

    [Test]
    public async Task BareConsumerDbContext_WithoutDispatchSource_ProviderDispatchNoOps()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions<BareConsumerDbContext>(
            dbName: _dbName,
            configure: builder => builder.AddInterceptors(_interceptor));

        await using var consumerCtx = new BareConsumerDbContext(options);
        consumerCtx.Products.Add(new Product { Name = "NoDispatch" });

        Assert.DoesNotThrowAsync(async () => await consumerCtx.SaveChangesAsync());

        await using var scope = _provider.CreateAsyncScope();
        var auditCtx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await auditCtx.AuditLogs.CountAsync(), Is.EqualTo(1));
    }

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditEnvelope> Envelopes { get; } = [];

        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default)
        {
            Envelopes.AddRange(envelopes);
            return Task.CompletedTask;
        }
    }

    private class BareConsumerDbContext(DbContextOptions<BareConsumerDbContext> options) : DbContext(options)
    {
        public DbSet<Product> Products { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private class BareConsumerDbContextWithContextSource(DbContextOptions<BareConsumerDbContextWithContextSource> options)
        : DbContext(options), IAuditContextSource
    {
        public DbSet<Product> Products { get; set; } = null!;

        public string? CurrentCorrelationId { get; set; }
        public string? CurrentIpAddress { get; set; }
        public string? CurrentUserAgent { get; set; }
        public string? CurrentUserId { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private class BareConsumerDbContextWithDispatchSource(DbContextOptions<BareConsumerDbContextWithDispatchSource> options)
        : DbContext(options), IAuditProviderDispatchSource
    {
        public DbSet<Product> Products { get; set; } = null!;

        public IServiceProvider? ScopedServiceProvider { get; set; }
        public bool IsDispatchingProviders { get; set; }
        public IReadOnlyList<PendingProviderDispatch>? PendingProviderDispatches { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Product>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private class Product
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    private interface ITestProductAuditProvider;
}
