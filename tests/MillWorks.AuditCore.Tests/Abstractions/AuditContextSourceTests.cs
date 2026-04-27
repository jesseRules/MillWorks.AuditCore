using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;

namespace MillWorks.AuditCore.Tests.Abstractions;

/// <summary>
/// Verifies that <see cref="IAuditContextSource"/> is the supported way for any
/// <c>DbContext</c> (not just <see cref="AuditDbContext"/>) to flow
/// request context (<c>UserId</c>, <c>CorrelationId</c>, <c>IpAddress</c>,
/// <c>UserAgent</c>) into <see cref="AuditEnvelope"/> instances published by
/// <see cref="AuditSaveChangesInterceptor"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class AuditContextSourceTests
{
    private RecordingSink _sink = null!;
    private ServiceProvider _provider = null!;
    private AuditSaveChangesInterceptor _interceptor = null!;

    [SetUp]
    public void Setup()
    {
        _sink = new RecordingSink();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton<IAuditSink>(_sink);
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
    public async Task ConsumerDbContext_ImplementingInterface_FlowsAllFourFieldsIntoEnvelope()
    {
        // A consumer DbContext that depends only on Abstractions for context fields,
        // not on AuditDbContext.
        var options = new DbContextOptionsBuilder<ConsumerContextSourceDbContext>()
            .UseInMemoryDatabase($"AuditContextSource_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(_interceptor)
            .Options;

        await using var ctx = new ConsumerContextSourceDbContext(options)
        {
            CurrentUserId = "user-42",
            CurrentCorrelationId = "corr-abc",
            CurrentIpAddress = "10.0.0.99",
            CurrentUserAgent = "agent/1.0",
        };

        ctx.Entities.Add(new ConsumerEntity { Name = "Hello" });
        await ctx.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.UserId, Is.EqualTo("user-42"));
            Assert.That(envelope.CorrelationId, Is.EqualTo("corr-abc"));
            Assert.That(envelope.IpAddress, Is.EqualTo("10.0.0.99"));
            Assert.That(envelope.UserAgent, Is.EqualTo("agent/1.0"));
        });
    }

    [Test]
    public async Task ConsumerDbContext_NotImplementingInterface_LeavesAllFourFieldsNull()
    {
        // A consumer DbContext that does NOT implement IAuditContextSource.
        // The cast in the interceptor returns null and all four fields stay null;
        // no exception, no synthetic defaults.
        var options = new DbContextOptionsBuilder<NoContextSourceDbContext>()
            .UseInMemoryDatabase($"NoContextSource_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(_interceptor)
            .Options;

        await using var ctx = new NoContextSourceDbContext(options);
        ctx.Entities.Add(new ConsumerEntity { Name = "Quiet" });
        await ctx.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.UserId, Is.Null);
            Assert.That(envelope.CorrelationId, Is.Null);
            Assert.That(envelope.IpAddress, Is.Null);
            Assert.That(envelope.UserAgent, Is.Null);
        });
    }

    [Test]
    public async Task ConsumerDbContext_PartiallyImplemented_OnlyPopulatedFieldsFlow()
    {
        // Implementations are free to return null for fields they do not provide
        // (e.g., a background-worker context that knows the correlation id but not
        // the IP / user-agent).
        var options = new DbContextOptionsBuilder<PartialContextSourceDbContext>()
            .UseInMemoryDatabase($"PartialContextSource_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(_interceptor)
            .Options;

        await using var ctx = new PartialContextSourceDbContext(options)
        {
            CurrentCorrelationId = "background-job-7",
        };

        ctx.Entities.Add(new ConsumerEntity { Name = "Background" });
        await ctx.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.CorrelationId, Is.EqualTo("background-job-7"));
            Assert.That(envelope.UserId, Is.Null);
            Assert.That(envelope.IpAddress, Is.Null);
            Assert.That(envelope.UserAgent, Is.Null);
        });
    }

    [Test]
    public void AuditDbContext_ImplementsIAuditContextSource()
    {
        // Smoke check: the existing audit-owned DbContext satisfies the interface so
        // the cast in the interceptor's hot path returns the same instance as before.
        Assert.That(typeof(IAuditContextSource).IsAssignableFrom(typeof(AuditDbContext)),
            Is.True,
            $"{nameof(AuditDbContext)} must implement {nameof(IAuditContextSource)} so " +
            "the cast in AuditSaveChangesInterceptor returns the existing context.");
    }

    // ── Test infrastructure ─────────────────────────────────────────────

    private sealed class RecordingSink : IAuditSink
    {
        public List<AuditEnvelope> Envelopes { get; } = [];

        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default)
        {
            Envelopes.Add(envelope);
            return Task.CompletedTask;
        }
    }

    private sealed class ConsumerContextSourceDbContext(
        DbContextOptions<ConsumerContextSourceDbContext> options)
        : DbContext(options), IAuditContextSource, IAuditBypassable
    {
        public DbSet<ConsumerEntity> Entities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

        public string? CurrentUserId { get; set; }
        public string? CurrentCorrelationId { get; set; }
        public string? CurrentIpAddress { get; set; }
        public string? CurrentUserAgent { get; set; }

        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConsumerEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class PartialContextSourceDbContext(
        DbContextOptions<PartialContextSourceDbContext> options)
        : DbContext(options), IAuditContextSource, IAuditBypassable
    {
        public DbSet<ConsumerEntity> Entities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

        public string? CurrentUserId => null;
        public string? CurrentCorrelationId { get; set; }
        public string? CurrentIpAddress => null;
        public string? CurrentUserAgent => null;

        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConsumerEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class NoContextSourceDbContext(
        DbContextOptions<NoContextSourceDbContext> options)
        : DbContext(options), IAuditBypassable
    {
        public DbSet<ConsumerEntity> Entities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConsumerEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class ConsumerEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }
}
