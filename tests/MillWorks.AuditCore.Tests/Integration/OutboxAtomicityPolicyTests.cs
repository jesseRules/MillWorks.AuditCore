using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Sinks;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// End-to-end coverage for the transactional-outbox hybrid atomicity policy in
/// <see cref="AuditOutboxWriter"/>, driven through the real interceptor against a SQLite
/// context (real transactions, no Docker required).
/// <para>
/// The policy: a mapped consumer context stages the outbox row on its change tracker so EF
/// saves it in the same <c>SaveChangesAsync</c> unit (atomic with no explicit transaction);
/// a bare context without an ambient transaction is rejected with
/// <see cref="AuditOutboxAtomicityException"/> rather than silently committing the audit row
/// independently of the business write.
/// </para>
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class OutboxAtomicityPolicyTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _sinkServices = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _sinkServices = BuildSinkServices();
    }

    [TearDown]
    public void TearDown()
    {
        _sinkServices?.Dispose();
        _connection?.Dispose();
    }

    [Test]
    public async Task MappedContext_SuccessfulSave_PersistsOutboxRowInSameUnit()
    {
        using (var setup = NewMappedContext(interceptor: null))
            await setup.Database.EnsureCreatedAsync();

        await using (var ctx = NewMappedContext(NewInterceptor()))
        {
            ctx.BusinessRecords.Add(new BusinessRecord { Name = "alice" });
            await ctx.SaveChangesAsync();
        }

        await using var verify = NewMappedContext(interceptor: null);
        Assert.Multiple(() =>
        {
            Assert.That(verify.BusinessRecords.Count(), Is.EqualTo(1));
            // Proves EF persists the outbox row that the interceptor staged during
            // SavingChangesAsync — i.e. it rides the same SaveChangesAsync unit of work.
            Assert.That(verify.AuditOutbox.Count(), Is.EqualTo(1),
                "Outbox row staged in the interceptor must be persisted by the same SaveChangesAsync");
        });
    }

    [Test]
    public void MappedContext_BusinessSaveFails_RollsBackOutboxRowAtomically()
    {
        var collidingId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        using (var setup = NewMappedContext(interceptor: null))
        {
            setup.Database.EnsureCreated();
            // Seed the colliding row WITHOUT the interceptor, so no outbox row exists yet.
            setup.BusinessRecords.Add(new BusinessRecord { Id = collidingId, Name = "original" });
            setup.SaveChanges();
        }

        using (var ctx = NewMappedContext(NewInterceptor()))
        {
            // Duplicate primary key: the business INSERT fails inside base.SaveChangesAsync,
            // after the interceptor has already staged the outbox row.
            ctx.BusinessRecords.Add(new BusinessRecord { Id = collidingId, Name = "duplicate" });
            Assert.ThrowsAsync<DbUpdateException>(async () => await ctx.SaveChangesAsync());
        }

        using var verify = NewMappedContext(interceptor: null);
        Assert.Multiple(() =>
        {
            Assert.That(verify.BusinessRecords.Count(), Is.EqualTo(1), "Only the seeded row should remain");
            Assert.That(verify.AuditOutbox.Count(), Is.Zero,
                "Outbox row must roll back atomically with the failed business write");
        });
    }

    [Test]
    public void BareContext_NoTransaction_FailsClosedAndCommitsNothing()
    {
        using (var setup = NewBareContext(interceptor: null))
            setup.Database.EnsureCreated();

        using (var ctx = NewBareContext(NewInterceptor()))
        {
            ctx.BusinessRecords.Add(new BusinessRecord { Name = "bob" });

            // No AuditOutboxEntity mapping and no ambient transaction → atomicity is impossible.
            // The guard throws, and (critically) it propagates even though the interceptor's
            // default failure mode is permissive — it must never be swallowed.
            Assert.ThrowsAsync<AuditOutboxAtomicityException>(async () => await ctx.SaveChangesAsync());
        }

        using var verify = NewBareContext(interceptor: null);
        Assert.That(verify.BusinessRecords.Count(), Is.Zero,
            "Business write must not commit when the outbox row cannot be written atomically");
    }

    [Test]
    public async Task MappedContext_DuplicateIdempotencyKeys_StagesOnlyOneOutboxRow()
    {
        var key = Guid.Parse("22222222-2222-2222-2222-222222222222");

        await using var ctx = NewMappedContext(interceptor: null);
        await ctx.Database.EnsureCreatedAsync();

        await using var scope = _sinkServices.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IConsumerDbContextAccessor>();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditOutboxWriter>();

        using (accessor.SetCurrent(ctx))
        {
            var written = await writer.WriteBatchAsync(
                [
                    ("{\"a\":1}", 1, key),
                    ("{\"a\":2}", 1, key)
                ]);

            Assert.That(written, Is.EqualTo(1));

            var replayWritten = await writer.WriteBatchAsync([("{\"a\":3}", 1, key)]);
            Assert.That(replayWritten, Is.Zero,
                "A retry while the first outbox row remains Added in the change tracker must be idempotent");
        }

        Assert.That(ctx.ChangeTracker.Entries<AuditOutboxEntity>()
            .Count(static e => e.State == EntityState.Added), Is.EqualTo(1));
    }

    [Test]
    public async Task MappedContext_PersistedDuplicateIdempotencyKey_IsTreatedAsSuccess()
    {
        var key = Guid.Parse("33333333-3333-3333-3333-333333333333");

        await using var ctx = NewMappedContext(interceptor: null);
        await ctx.Database.EnsureCreatedAsync();
        ctx.AuditOutbox.Add(new AuditOutboxEntity
        {
            EnvelopeJson = "{\"existing\":true}",
            EnvelopeVersion = 1,
            IdempotencyKey = key
        });
        await ctx.SaveChangesAsync();

        await using var scope = _sinkServices.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IConsumerDbContextAccessor>();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditOutboxWriter>();

        using (accessor.SetCurrent(ctx))
        {
            var written = await writer.WriteBatchAsync([("{\"replay\":true}", 1, key)]);
            Assert.That(written, Is.Zero);
        }

        Assert.That(await ctx.AuditOutbox.CountAsync(o => o.IdempotencyKey == key), Is.EqualTo(1));
    }

    [Test]
    public async Task MappedContext_SchemaMismatch_FailsClosed()
    {
        await using var ctx = NewMismatchedMappedContext();
        await ctx.Database.EnsureCreatedAsync();

        await using var scope = _sinkServices.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IConsumerDbContextAccessor>();
        var writer = scope.ServiceProvider.GetRequiredService<IAuditOutboxWriter>();

        using (accessor.SetCurrent(ctx))
        {
            var ex = Assert.ThrowsAsync<AuditOutboxAtomicityException>(
                async () => await writer.WriteBatchAsync([("{\"a\":1}", 1, Guid.NewGuid())]));

            Assert.That(ex!.Message, Does.Contain("mapped to schema"));
        }
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private ServiceProvider BuildSinkServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(new EntityFrameworkOptions { Schema = "audit" }));
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddScoped<IAuditOutboxWriter, AuditOutboxWriter>();
        services.AddScoped<IAuditSink, TransactionalOutboxSink>();
        return services.BuildServiceProvider();
    }

    private AuditSaveChangesInterceptor NewInterceptor() =>
        new(Mock.Of<ILogger<AuditSaveChangesInterceptor>>(),
            scopeFactory: _sinkServices.GetRequiredService<IServiceScopeFactory>());

    private MappedConsumerDbContext NewMappedContext(AuditSaveChangesInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<MappedConsumerDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new MappedConsumerDbContext(builder.Options);
    }

    private BareConsumerDbContext NewBareContext(AuditSaveChangesInterceptor? interceptor)
    {
        var builder = new DbContextOptionsBuilder<BareConsumerDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        if (interceptor is not null)
            builder.AddInterceptors(interceptor);
        return new BareConsumerDbContext(builder.Options);
    }

    private MismatchedMappedConsumerDbContext NewMismatchedMappedContext()
    {
        var builder = new DbContextOptionsBuilder<MismatchedMappedConsumerDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new MismatchedMappedConsumerDbContext(builder.Options);
    }

    // ── Test fixture types ─────────────────────────────────────────────────────

    /// <summary>Consumer context that maps AuditOutboxEntity (inherited from AuditDbContext).</summary>
    private sealed class MappedConsumerDbContext : AuditDbContext
    {
        public MappedConsumerDbContext(DbContextOptions<MappedConsumerDbContext> options)
            : base(options)
        {
        }

        public DbSet<BusinessRecord> BusinessRecords { get; set; } = null!;
    }

    /// <summary>Bare consumer context that does NOT map AuditOutboxEntity.</summary>
    private sealed class BareConsumerDbContext : DbContext
    {
        public BareConsumerDbContext(DbContextOptions<BareConsumerDbContext> options)
            : base(options)
        {
        }

        public DbSet<BusinessRecord> BusinessRecords { get; set; } = null!;
    }

    /// <summary>Consumer context that maps AuditOutboxEntity to a schema the drainer is not configured to read.</summary>
    private sealed class MismatchedMappedConsumerDbContext : DbContext
    {
        public MismatchedMappedConsumerDbContext(DbContextOptions<MismatchedMappedConsumerDbContext> options)
            : base(options)
        {
        }

        public DbSet<AuditOutboxEntity> AuditOutbox { get; set; } = null!;
        public DbSet<BusinessRecord> BusinessRecords { get; set; } = null!;

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<AuditOutboxEntity>().ToTable("AuditOutbox", "other");
            modelBuilder.Entity<BusinessRecord>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class BusinessRecord
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }
}
