using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Constants;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;

namespace MillWorks.AuditCore.Tests.EntityFramework;

/// <summary>
/// Verifies that <see cref="AuditSaveChangesInterceptor"/> publishes
/// <see cref="AuditEnvelope"/> instances through <see cref="IAuditSink"/>
/// rather than writing <see cref="AuditLogEntity"/> rows directly to the
/// saving DbContext. Pins the post-Phase-03 contract: one envelope per
/// entity entry, FERPA tagging on description and AdditionalData, fail-closed
/// propagation when the sink throws.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class InterceptorSinkRoutingTests
{
    private RecordingSink _sink = null!;
    private ServiceProvider _provider = null!;
    private AuditSaveChangesInterceptor _interceptor = null!;
    private RoutingTestDbContext _dbContext = null!;

    [SetUp]
    public void Setup()
    {
        _sink = new RecordingSink();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditSink>(_sink);
        _provider = services.BuildServiceProvider();

        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        _interceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            scopeFactory: scopeFactory);

        var options = new DbContextOptionsBuilder<RoutingTestDbContext>()
            .UseInMemoryDatabase($"InterceptorSinkRouting_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(_interceptor)
            .Options;

        _dbContext = new RoutingTestDbContext(options);
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _provider.Dispose();
    }

    [Test]
    public async Task AddedEntity_PublishesOneEnvelope_WithSnapshotInAdditionalData()
    {
        var entity = new RoutingEntity { Name = "Created" };
        _dbContext.Entities.Add(entity);

        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
            Assert.That(envelope.EntityName, Is.EqualTo("RoutingEntity"));
            Assert.That(envelope.Action, Is.EqualTo(AuditAction.Created));
            Assert.That(envelope.EntityId, Is.EqualTo(entity.Id));
            Assert.That(envelope.PropertyChanges, Is.Null,
                "Added entries do not carry per-property diffs.");
            Assert.That(envelope.AdditionalData, Is.Not.Null);
            Assert.That(envelope.AdditionalData!, Does.Contain("\"Name\""));
            Assert.That(envelope.AdditionalData!, Does.Contain("Created"));
            Assert.That(envelope.Description, Is.EqualTo("Added RoutingEntity"));
        });
    }

    [Test]
    public async Task ModifiedEntity_PublishesOneEnvelope_WithPropertyChangesList()
    {
        var entity = new RoutingEntity { Name = "Original" };
        _dbContext.Entities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _sink.Envelopes.Clear();

        var tracked = await _dbContext.Entities.FindAsync(entity.Id);
        tracked!.Name = "Updated";

        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1),
            "Expect ONE envelope per entity entry, regardless of property count.");
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
            Assert.That(envelope.Action, Is.EqualTo(AuditAction.Updated));
            Assert.That(envelope.PropertyChanges, Is.Not.Null);
            Assert.That(envelope.PropertyChanges!, Has.Count.EqualTo(1));
            Assert.That(envelope.PropertyChanges![0].PropertyName, Is.EqualTo("Name"));
            Assert.That(envelope.PropertyChanges[0].OldValue, Is.EqualTo("Original"));
            Assert.That(envelope.PropertyChanges[0].NewValue, Is.EqualTo("Updated"));
            Assert.That(envelope.Description, Is.EqualTo("Updated RoutingEntity"));
        });
    }

    [Test]
    public async Task DeletedEntity_PublishesOneEnvelope_WithSnapshotInAdditionalData()
    {
        var entity = new RoutingEntity { Name = "ToDelete" };
        _dbContext.Entities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _sink.Envelopes.Clear();

        _dbContext.Entities.Remove(entity);
        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Action, Is.EqualTo(AuditAction.Deleted));
            Assert.That(envelope.PropertyChanges, Is.Null);
            Assert.That(envelope.AdditionalData, Is.Not.Null);
            Assert.That(envelope.Description, Is.EqualTo("Deleted RoutingEntity"));
        });
    }

    [Test]
    public async Task ModifiedFerpaEntity_EnvelopeDescriptionCarriesFerpaPrefix_AndFerpaMetadataInAdditionalData()
    {
        var entity = new RoutingFerpaEntity { Name = "OriginalFerpa", StudentId = "S100" };
        _dbContext.FerpaEntities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
        _sink.Envelopes.Clear();

        var tracked = await _dbContext.FerpaEntities.FindAsync(entity.Id);
        tracked!.Name = "ModifiedFerpa";

        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Description, Does.StartWith("[FERPA] "));
            Assert.That(envelope.Description, Does.Contain("RoutingFerpaEntity"));
            Assert.That(envelope.AdditionalData, Is.Not.Null);
            Assert.That(envelope.AdditionalData!, Does.Contain("_FerpaEventType"));
            Assert.That(envelope.AdditionalData!, Does.Contain("_ConsentRequired"));
            Assert.That(envelope.PropertyChanges, Has.Count.EqualTo(1));
            Assert.That(envelope.PropertyChanges![0].PropertyName, Is.EqualTo("Name"));
        });
    }

    [Test]
    public async Task AddedFerpaEntity_EnvelopeCarriesFerpaPrefixAndSnapshotMetadata()
    {
        var entity = new RoutingFerpaEntity { Name = "AddedFerpa", StudentId = "S101" };
        _dbContext.FerpaEntities.Add(entity);

        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Has.Count.EqualTo(1));
        var envelope = _sink.Envelopes[0];
        Assert.Multiple(() =>
        {
            Assert.That(envelope.Description, Does.StartWith("[FERPA] Added "));
            Assert.That(envelope.AdditionalData, Is.Not.Null);
            Assert.That(envelope.AdditionalData!, Does.Contain("_FerpaEventType"));
            Assert.That(envelope.AdditionalData!,
                Does.Contain(FerpaEventTypes.EventTypeBuilder.Build("RoutingFerpaEntity", "Added")));
            Assert.That(envelope.AdditionalData!, Does.Contain("_RecordType"));
        });
    }

    [Test]
    public async Task ModifiedEntity_AllPropertiesUnchanged_PublishesNoEnvelope()
    {
        var entity = new RoutingEntity { Name = "Stable" };
        _dbContext.Entities.Add(entity);
        await _dbContext.SaveChangesAsync();
        _sink.Envelopes.Clear();

        // Force EF into Modified state without any property change.
        _dbContext.Entry(entity).State = EntityState.Modified;
        await _dbContext.SaveChangesAsync();

        Assert.That(_sink.Envelopes, Is.Empty,
            "Modified entry with no surviving diffs must not produce an envelope.");
    }

    [Test]
    public Task SinkThrows_FailClosedRegulatedEntity_RethrowsAsAuditIntegrityException()
    {
        // Replace the recording sink with a throwing sink, fresh DI graph,
        // FailClosedForRegulated mode, FERPA entity → fail-closed must rethrow.
        _provider.Dispose();

        var throwingSink = new ThrowingSink(new InvalidOperationException("simulated sink failure"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditSink>(throwingSink);
        _provider = services.BuildServiceProvider();
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        var failClosedInterceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            failureMode: AuditFailureMode.FailClosedForRegulated,
            scopeFactory: scopeFactory);

        _dbContext.Dispose();
        var options = new DbContextOptionsBuilder<RoutingTestDbContext>()
            .UseInMemoryDatabase($"InterceptorSinkRoutingFailClosed_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(failClosedInterceptor)
            .Options;
        _dbContext = new RoutingTestDbContext(options);

        _dbContext.FerpaEntities.Add(new RoutingFerpaEntity { Name = "FerpaToFail", StudentId = "S102" });

        var ex = Assert.ThrowsAsync<AuditIntegrityException>(
            async () => await _dbContext.SaveChangesAsync());
        Assert.That(ex!.InnerException, Is.TypeOf<InvalidOperationException>());
        Assert.That(ex.Message, Does.Contain("audit log records"));

        return Task.CompletedTask;
    }

    [Test]
    public async Task SinkThrows_PermissiveMode_SwallowsAndContinues()
    {
        _provider.Dispose();

        var throwingSink = new ThrowingSink(new InvalidOperationException("simulated sink failure"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IAuditSink>(throwingSink);
        _provider = services.BuildServiceProvider();
        var scopeFactory = _provider.GetRequiredService<IServiceScopeFactory>();

        var permissiveInterceptor = new AuditSaveChangesInterceptor(
            NullLogger<AuditSaveChangesInterceptor>.Instance,
            failureMode: AuditFailureMode.Permissive,
            scopeFactory: scopeFactory);

        _dbContext.Dispose();
        var options = new DbContextOptionsBuilder<RoutingTestDbContext>()
            .UseInMemoryDatabase($"InterceptorSinkRoutingPermissive_{Guid.NewGuid()}")
            .ConfigureWarnings(static w =>
            {
                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning);
            })
            .AddInterceptors(permissiveInterceptor)
            .Options;
        _dbContext = new RoutingTestDbContext(options);

        _dbContext.Entities.Add(new RoutingEntity { Name = "Permissive" });

        Assert.DoesNotThrowAsync(async () => await _dbContext.SaveChangesAsync());

        // Confirm the business save still committed (the entity row exists).
        var saved = await _dbContext.Entities.AsNoTracking().FirstOrDefaultAsync();
        Assert.That(saved, Is.Not.Null);
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

    private sealed class ThrowingSink(Exception toThrow) : IAuditSink
    {
        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default)
        {
            throw toThrow;
        }
    }

    private sealed class RoutingTestDbContext(DbContextOptions<RoutingTestDbContext> options)
        : DbContext(options), IAuditBypassable
    {
        public DbSet<RoutingEntity> Entities { get; set; } = null!;
        public DbSet<RoutingFerpaEntity> FerpaEntities { get; set; } = null!;
        public DbSet<AuditLogEntity> AuditLogs { get; set; } = null!;

        public bool BypassAuditInterceptor { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<RoutingEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<RoutingFerpaEntity>().HasKey(static e => e.Id);
            modelBuilder.Entity<AuditLogEntity>().HasKey(static e => e.Id);
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class RoutingEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
    }

    [FERPA(RequiresConsent = true, RecordType = "EducationRecord")]
    private sealed class RoutingFerpaEntity
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string StudentId { get; set; } = string.Empty;
    }
}
