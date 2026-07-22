using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Verifies the <see cref="IAuditPropertySensitivityPolicy"/> seam: a registered policy can tighten
/// a property's audit treatment beyond what AuditCore's attributes declare, on both the update
/// (per-property) and create (snapshot) interceptor paths, using "strictest wins" so a policy can
/// never loosen an attribute-declared treatment. The no-policy control proves the interceptor's
/// default behaviour is unchanged when no policy is registered.
/// </summary>
[TestFixture]
public sealed class InterceptorSensitivityPolicyTests
{
    private const string SentinelName = "sentinel-name-9c1f";
    private const string SentinelSecret = "sentinel-secret-9c1f";

    private ServiceProvider _provider = null!;

    [TearDown]
    public void TearDown() => _provider?.Dispose();

    /// <summary>
    /// Builds an audited <see cref="PolicyTestDbContext"/> wired with the given policies. The audit
    /// writer and the test context share one in-memory database name, so rows the writer persists
    /// through <see cref="AuditDbContext"/> are visible via the test context's AuditLogs set.
    /// </summary>
    private PolicyTestDbContext BuildContext(params IAuditPropertySensitivityPolicy[] policies)
    {
        var dbName = $"PolicyTestDb_{Guid.NewGuid()}";

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

        var interceptor = new AuditSaveChangesInterceptor(
            Mock.Of<ILogger<AuditSaveChangesInterceptor>>(),
            scopeFactory: scopeFactory,
            sensitivityPolicies: policies);

        var options = TestDbContextFactory.CreateInMemoryOptions<PolicyTestDbContext>(
            dbName: dbName,
            configure: builder => builder.AddInterceptors(interceptor));

        return new PolicyTestDbContext(options);
    }

    [Test]
    public async Task Create_WithoutPolicy_WritesPlainValue()
    {
        // Control: the seam is inert when no policy is registered — the plain value is captured.
        await using var db = BuildContext();
        db.Entities.Add(new PolicyTestEntity { Name = SentinelName, Secret = SentinelSecret });
        await db.SaveChangesAsync();

        var created = await db.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == nameof(PolicyTestEntity) && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(created.AdditionalData, Does.Contain(SentinelName),
            "Without a policy the interceptor must behave exactly as before — plain value captured.");
    }

    [Test]
    public async Task Create_WithPolicy_MasksAndOmitsClassifiedProperties()
    {
        // Policy masks Name and omits Secret — neither plaintext may reach the create snapshot.
        await using var db = BuildContext(new NameMaskSecretOmitPolicy());
        db.Entities.Add(new PolicyTestEntity { Name = SentinelName, Secret = SentinelSecret });
        await db.SaveChangesAsync();

        var created = await db.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == nameof(PolicyTestEntity) && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(created.AdditionalData, Does.Not.Contain(SentinelName), "Name should be masked.");
            Assert.That(created.AdditionalData, Does.Contain("***"), "Masked Name should render as ***.");
            Assert.That(created.AdditionalData, Does.Not.Contain(SentinelSecret), "Secret should be omitted.");
            Assert.That(created.AdditionalData, Does.Not.Contain(nameof(PolicyTestEntity.Secret)),
                "An omitted property should not appear in the snapshot at all.");
        });
    }

    [Test]
    public async Task Update_WithPolicy_MasksOldAndNewValues()
    {
        await using var db = BuildContext(new NameMaskSecretOmitPolicy());
        var entity = new PolicyTestEntity { Name = SentinelName, Secret = SentinelSecret };
        db.Entities.Add(entity);
        await db.SaveChangesAsync();

        entity.Name = SentinelName + "-updated";
        await db.SaveChangesAsync();

        var updateLog = await db.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == nameof(PolicyTestEntity)
                               && l.Action == AuditAction.Updated
                               && l.PropertyName == nameof(PolicyTestEntity.Name))
            .FirstAsync();

        Assert.Multiple(() =>
        {
            Assert.That(updateLog.OldValue, Is.EqualTo("***"));
            Assert.That(updateLog.NewValue, Is.EqualTo("***"));
        });
    }

    [Test]
    public async Task Policy_CannotLoosen_AttributeDeclaredMask()
    {
        // The attribute already masks Secret; a policy that returns the weakest treatment (Audit)
        // must not downgrade it — strictest wins.
        await using var db = BuildContext(new DowngradeEverythingToAuditPolicy());
        db.Entities.Add(new PolicyTestEntity { Name = SentinelName, MaskedByAttribute = SentinelSecret });
        await db.SaveChangesAsync();

        var created = await db.AuditLogs.AsNoTracking()
            .Where(static l => l.EntityName == nameof(PolicyTestEntity) && l.Action == AuditAction.Created)
            .FirstAsync();

        Assert.That(created.AdditionalData, Does.Not.Contain(SentinelSecret),
            "A policy returning Audit must not loosen an attribute-declared mask.");
    }

    // ── Test policies ────────────────────────────────────────────────────────────────────────
    private sealed class NameMaskSecretOmitPolicy : IAuditPropertySensitivityPolicy
    {
        public AuditFieldTreatment? Classify(in AuditPropertyRef property)
        {
            if (property.EntityType != typeof(PolicyTestEntity)) return null;
            return property.PropertyName switch
            {
                nameof(PolicyTestEntity.Name) => AuditFieldTreatment.Mask,
                nameof(PolicyTestEntity.Secret) => AuditFieldTreatment.Omit,
                _ => null
            };
        }
    }

    private sealed class DowngradeEverythingToAuditPolicy : IAuditPropertySensitivityPolicy
    {
        public AuditFieldTreatment? Classify(in AuditPropertyRef property) => AuditFieldTreatment.Audit;
    }

    // ── Test context + entity ────────────────────────────────────────────────────────────────
    private sealed class PolicyTestEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;

        [SensitiveData(MaskInLogs = true)]
        public string MaskedByAttribute { get; set; } = string.Empty;
    }

    private sealed class PolicyTestDbContext(DbContextOptions<PolicyTestDbContext> options)
        : DbContext(options), IAuditBypassable
    {
        public DbSet<PolicyTestEntity> Entities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;
        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<PolicyTestEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }
}
