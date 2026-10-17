using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks.Identity;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Enums;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Interceptors;
using MillWorks.AuditCore.EntityFramework.Sinks;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
public sealed class InterceptorMutationIdentityTests
{
    [Test]
    public async Task SeparateCreationsWithReusedTemporaryKeysHaveDifferentIdentities()
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = Options(connection, provider);
        await using (var setup = new MutationContext(options)) await setup.Database.EnsureCreatedAsync();
        for (var i = 0; i < 2; i++)
        {
            await using var db = new MutationContext(options);
            var entry = db.Add(new ClaimRow { Value = "same-permission" });
            entry.Property(x => x.Id).CurrentValue = -123;
            entry.Property(x => x.Id).IsTemporary = true;
            await db.SaveChangesAsync();
        }
        Assert.That(sink.Envelopes, Has.Count.EqualTo(2));
        Assert.That(sink.Envelopes[0].EnvelopeId, Is.Not.EqualTo(sink.Envelopes[1].EnvelopeId));
        Assert.That(sink.Envelopes.All(static e => e.EntityIdString is null), Is.True);
        Assert.That(sink.Envelopes.All(static e => !e.AdditionalData!.Contains("-123")), Is.True);
    }

    [Test]
    public async Task ReturningToTheSameValuesAfterSuccessfulSavesIsANewMutation()
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MutationContext(Options(connection, provider));
        await db.Database.EnsureCreatedAsync();
        var row = new ClaimRow { Value = "before" };
        db.Add(row);
        await db.SaveChangesAsync();
        row.Value = "after";
        await db.SaveChangesAsync();
        var firstUpdate = sink.Envelopes[^1].EnvelopeId;
        row.Value = "before";
        await db.SaveChangesAsync();
        row.Value = "after";
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes[^1].EnvelopeId, Is.Not.EqualTo(firstUpdate));
    }

    [Test]
    public async Task FailedSaveRetriesReuseThePendingMutationIdentity()
    {
        var sink = new CaptureSink { FailNext = true };
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MutationContext(Options(connection, provider));
        await db.Database.EnsureCreatedAsync();
        db.Add(new ClaimRow { Value = "retry" });
        await Assert.ThrowsAsync<AuditIntegrityException>(() => db.SaveChangesAsync());
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes, Has.Count.EqualTo(2));
        Assert.That(sink.Envelopes[0].EnvelopeId, Is.EqualTo(sink.Envelopes[1].EnvelopeId));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RetainedTokenSurvivesReplacementContextAndGeneratedKeyFixup(bool keyAlreadyAssigned)
    {
        var sink = new CaptureSink { FailNext = true };
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = Options(connection, provider);
        AuditMutationToken token;
        await using (var first = new MutationContext(options))
        {
            await first.Database.EnsureCreatedAsync();
            var row = new ClaimRow { Value = "same-command" };
            var entry = first.Add(row);
            entry.Property(x => x.Id).CurrentValue = -123;
            entry.Property(x => x.Id).IsTemporary = true;
            token = AuditMutationIdentity.Capture(first, row);
            await Assert.ThrowsAsync<AuditIntegrityException>(() => first.SaveChangesAsync());
        }
        // Token round-trip models a retained command checkpoint; no tracked entity/context is retained.
        token = System.Text.Json.JsonSerializer.Deserialize<AuditMutationToken>(System.Text.Json.JsonSerializer.Serialize(token))!;
        await using (var retry = new MutationContext(options))
        {
            var row = new ClaimRow { Id = keyAlreadyAssigned ? 987 : 0, Value = "same-command" };
            var entry = retry.Add(row);
            if (!keyAlreadyAssigned)
            {
                entry.Property(x => x.Id).CurrentValue = -456;
                entry.Property(x => x.Id).IsTemporary = true;
            }
            AuditMutationIdentity.Restore(retry, row, token);
            await retry.SaveChangesAsync();
        }
        Assert.That(sink.Envelopes, Has.Count.EqualTo(2));
        Assert.That(sink.Envelopes[1].EnvelopeId, Is.EqualTo(token.Id));
        Assert.That(AuditEnvelopeContent.Fingerprint(sink.Envelopes[1]),
            Is.EqualTo(AuditEnvelopeContent.Fingerprint(sink.Envelopes[0])));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RolledBackBusinessSaveCanBeRetriedWithAnExplicitlyRetainedToken(bool retainToken)
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = Options(connection, provider);
        AuditMutationToken token;
        await using (var first = new MutationContext(options))
        {
            await first.Database.EnsureCreatedAsync();
            var row = new ClaimRow { Value = "rollback" };
            first.Add(row);
            token = AuditMutationIdentity.Capture(first, row);
            await using var transaction = await first.Database.BeginTransactionAsync();
            await first.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using (var retry = new MutationContext(options))
        {
            var row = new ClaimRow { Value = "rollback" };
            retry.Add(row);
            if (retainToken) AuditMutationIdentity.Restore(retry, row, token);
            await retry.SaveChangesAsync();
            Assert.That(await retry.Set<ClaimRow>().CountAsync(), Is.EqualTo(1));
        }
        Assert.That(sink.Envelopes, Has.Count.EqualTo(2));
        Assert.That(sink.Envelopes[1].EnvelopeId == token.Id, Is.EqualTo(retainToken));
    }

    [Test]
    public async Task ChangedRetryKeepsIdentityButChangesComparisonEvidence()
    {
        var sink = new CaptureSink { FailNext = true };
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MutationContext(Options(connection, provider));
        await db.Database.EnsureCreatedAsync();
        var row = new ClaimRow { Value = "original" };
        db.Add(row);
        await Assert.ThrowsAsync<AuditIntegrityException>(() => db.SaveChangesAsync());
        row.Value = "changed";
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes[0].EnvelopeId, Is.EqualTo(sink.Envelopes[1].EnvelopeId));
        Assert.That(AuditEnvelopeContent.Fingerprint(sink.Envelopes[0]),
            Is.Not.EqualTo(AuditEnvelopeContent.Fingerprint(sink.Envelopes[1])));
    }

    [Test]
    public async Task IdenticalRowsInOneSaveStillHaveDistinctMutationIdentities()
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MutationContext(Options(connection, provider));
        await db.Database.EnsureCreatedAsync();
        db.AddRange(new ClaimRow { Value = "same" }, new ClaimRow { Value = "same" });
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes.Select(static e => e.EnvelopeId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(sink.Envelopes.Select(static e => e.AdditionalData).Distinct().Count(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task DurableAuditAcceptanceSurvivesLostAcknowledgementAndRejectsChangedRetry(bool changed)
    {
        await using var auditConnection = new SqliteConnection("Data Source=:memory:");
        await auditConnection.OpenAsync();
        await using var auditProvider = new ServiceCollection()
            .AddDbContext<AuditDbContext>(o => o.UseSqlite(auditConnection))
            .BuildServiceProvider();
        await using (var setup = auditProvider.CreateAsyncScope())
            await setup.ServiceProvider.GetRequiredService<AuditDbContext>().Database.EnsureCreatedAsync();
        var writer = new AuditEntityBatchWriter(
            auditProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance);
        var sink = new CaptureSink
        {
            FailNext = true,
            OnPublish = async envelopes =>
            {
                var outcomes = await writer.WriteBatchAsync(envelopes, CancellationToken.None);
                var failed = outcomes.FirstOrDefault(static result => !result.Succeeded);
                if (failed is not null) throw failed.Exception ?? new InvalidOperationException(failed.ErrorMessage);
            }
        };
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = Options(connection, provider);
        AuditMutationToken token;
        await using (var first = new MutationContext(options))
        {
            await first.Database.EnsureCreatedAsync();
            var row = new ClaimRow { Value = "accepted" };
            first.Add(row);
            token = AuditMutationIdentity.Capture(first, row);
            await Assert.ThrowsAsync<AuditIntegrityException>(() => first.SaveChangesAsync());
            Assert.That(await first.Set<ClaimRow>().CountAsync(), Is.Zero);
        }
        await using (var retry = new MutationContext(options))
        {
            var row = new ClaimRow { Value = changed ? "different" : "accepted" };
            retry.Add(row);
            AuditMutationIdentity.Restore(retry, row, token);
            if (changed)
                await Assert.ThrowsAsync<AuditIntegrityException>(() => retry.SaveChangesAsync());
            else await retry.SaveChangesAsync();
            Assert.That(await retry.Set<ClaimRow>().CountAsync(), Is.EqualTo(changed ? 0 : 1));
        }
        await using var verification = auditProvider.CreateAsyncScope();
        var audit = verification.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await audit.AuditLogs.CountAsync(), Is.EqualTo(1));
        Assert.That(await audit.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task HistoricalTemporaryKeyAuditRemainsUnverifiedAndDoesNotBlockANewMutation()
    {
        var historicalId = Guid.Parse("5e73fab6-3b86-a9cf-f598-07659da32ff2");
        await using var auditConnection = new SqliteConnection("Data Source=:memory:");
        await auditConnection.OpenAsync();
        await using var auditProvider = new ServiceCollection()
            .AddDbContext<AuditDbContext>(o => o.UseSqlite(auditConnection))
            .BuildServiceProvider();
        await using (var setup = auditProvider.CreateAsyncScope())
        {
            var audit = setup.ServiceProvider.GetRequiredService<AuditDbContext>();
            await audit.Database.EnsureCreatedAsync();
            audit.AuditLogs.Add(new AuditLogEntity
            {
                EnvelopeId = historicalId, EntityName = nameof(ClaimRow),
                Action = AuditAction.Created,
                AdditionalData = "{\"Id\":\"-123\",\"Value\":\"same-permission\"}"
            });
            await audit.SaveChangesAsync();
        }
        var writer = new AuditEntityBatchWriter(
            auditProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance);
        var sink = new CaptureSink
        {
            OnPublish = async envelopes =>
            {
                var outcomes = await writer.WriteBatchAsync(envelopes, CancellationToken.None);
                Assert.That(outcomes.All(static result => result.Succeeded), Is.True);
            }
        };
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new MutationContext(Options(connection, provider));
        await db.Database.EnsureCreatedAsync();
        var entry = db.Add(new ClaimRow { Value = "same-permission" });
        entry.Property(x => x.Id).CurrentValue = -123;
        entry.Property(x => x.Id).IsTemporary = true;
        await db.SaveChangesAsync();
        await using var verification = auditProvider.CreateAsyncScope();
        var verify = verification.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await verify.AuditLogs.CountAsync(), Is.EqualTo(2));
        Assert.That(await verify.Set<AuditEnvelopeIdentity>()
            .AnyAsync(x => x.LogicalId == historicalId), Is.False);
        var old = sink.Envelopes.Single() with { EnvelopeId = historicalId };
        var rejected = await writer.WriteBatchAsync([old], CancellationToken.None);
        Assert.That(rejected.Single().ErrorMessage, Is.EqualTo("audit_identity_unverified"));
    }

    [Test]
    public async Task InternalAuditEvidenceDoesNotGenerateRecursiveEntityAudits()
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var interceptor = new AuditSaveChangesInterceptor(NullLogger<AuditSaveChangesInterceptor>.Instance,
            failureMode: AuditFailureMode.FailClosedAlways, scopeFactory: provider.GetRequiredService<IServiceScopeFactory>());
        await using var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection)
            .AddInterceptors(interceptor).Options);
        await db.Database.EnsureCreatedAsync();
        var source = new AuditOutboxEntity { IdempotencyKey = Guid.NewGuid(), EnvelopeJson = "{}" };
        var log = new AuditLogEntity { EntityName = "Claim", Action = AuditAction.Created };
        db.AddRange(source, log,
            new AuditLogSubjectLinkEntity { AuditLog = log, TenantId = Guid.NewGuid(), SubjectId = Guid.NewGuid(), Relationship = "Owner" },
            new AuditOutboxOperation { Id = Guid.NewGuid(), Kind = "claim", RequestHash = "hash", ResultJson = "{}" },
            new AuditOutboxResolution { Id = Guid.NewGuid(), OutboxRowId = source.Id, ActorId = "operator", Reason = "reviewed", Action = "skip" },
            new AuditOutboxFailureHandoff { Id = Guid.NewGuid(), OutboxRowId = source.Id, EnvelopeJson = "{}", ReasonCode = "test" });
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes, Is.Empty);
        Assert.That(await db.Set<AuditOutboxOperation>().CountAsync(), Is.EqualTo(1));
        Assert.That(await db.Set<AuditOutboxFailureHandoff>().CountAsync(), Is.EqualTo(1));
        Assert.That(await db.Set<AuditOutboxResolution>().CountAsync(), Is.EqualTo(1));
        Assert.That(await db.Set<AuditLogSubjectLinkEntity>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task PooledContextLeaseCannotReuseAnEarlierFailedMutationsIdentity()
    {
        var services = new ServiceCollection();
        services.AddDbContextPool<MutationContext>(o => o.UseSqlite("Data Source=:memory:"), poolSize: 1);
        await using var provider = services.BuildServiceProvider();
        var row = new ClaimRow { Value = "pending" };
        MutationContext firstInstance;
        AuditMutationToken firstToken;
        await using (var scope = provider.CreateAsyncScope())
        {
            firstInstance = scope.ServiceProvider.GetRequiredService<MutationContext>();
            firstInstance.Add(row);
            firstToken = AuditMutationIdentity.Capture(firstInstance, row);
        }
        await using (var scope = provider.CreateAsyncScope())
        {
            var next = scope.ServiceProvider.GetRequiredService<MutationContext>();
            Assert.That(ReferenceEquals(next, firstInstance), Is.True);
            next.Add(row);
            Assert.That(AuditMutationIdentity.Capture(next, row).Id, Is.Not.EqualTo(firstToken.Id));
        }
    }

    [Test]
    public async Task CancellationAfterPublicationRetainsThePendingIdentity()
    {
        var sink = new CaptureSink();
        await using var provider = Provider(sink);
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        using var cancellation = new CancellationTokenSource();
        await using var db = new MutationContext(Options(connection, provider, new CancelOnce(cancellation)));
        await db.Database.EnsureCreatedAsync();
        db.Add(new ClaimRow { Value = "cancelled-save" });
        await Assert.ThrowsAsync<OperationCanceledException>(() => db.SaveChangesAsync(cancellation.Token));
        await db.SaveChangesAsync();
        Assert.That(sink.Envelopes, Has.Count.EqualTo(2));
        Assert.That(sink.Envelopes[1].EnvelopeId, Is.EqualTo(sink.Envelopes[0].EnvelopeId));
    }

    private sealed class CancelOnce(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private bool _cancel = true;
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_cancel)
            {
                _cancel = false;
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return ValueTask.FromResult(result);
        }
    }

    private static ServiceProvider Provider(CaptureSink sink)
    {
        var services = new ServiceCollection();
        services.AddScoped<IConsumerDbContextAccessor, ConsumerDbContextAccessor>();
        services.AddSingleton<IAuditSink>(sink);
        return services.BuildServiceProvider();
    }

    private static DbContextOptions<MutationContext> Options(SqliteConnection connection, ServiceProvider provider,
        params IInterceptor[] additional) =>
        new DbContextOptionsBuilder<MutationContext>().UseSqlite(connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(NullLogger<AuditSaveChangesInterceptor>.Instance,
                failureMode: AuditFailureMode.FailClosedAlways,
                scopeFactory: provider.GetRequiredService<IServiceScopeFactory>()))
            .AddInterceptors(additional).Options;

    private sealed class MutationContext : DbContext
    {
        public MutationContext(DbContextOptions<MutationContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.Entity<ClaimRow>().HasKey(x => x.Id);
    }
    private sealed class ClaimRow
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }
    private sealed class CaptureSink : IAuditSink
    {
        public List<AuditEnvelope> Envelopes { get; } = [];
        public bool FailNext { get; set; }
        public Func<IReadOnlyList<AuditEnvelope>, Task>? OnPublish { get; init; }
        public Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default) => PublishBatchAsync([envelope], cancellationToken);
        public async Task PublishBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken = default)
        {
            Envelopes.AddRange(envelopes);
            if (OnPublish is not null) await OnPublish(envelopes);
            if (FailNext) { FailNext = false; throw new InvalidOperationException("Injected publication failure."); }
        }
    }
}
