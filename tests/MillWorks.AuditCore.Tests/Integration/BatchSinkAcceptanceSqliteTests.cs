using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Integration;

[TestFixture]
[Category("Integration")]
public sealed class BatchSinkAcceptanceSqliteTests : SqliteIntegrationFixture
{
    [TearDown]
    public async Task DropFaultTriggers()
    {
        await using var context = CreateContext();
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS RejectEntity");
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS RejectLastEvent");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MixedBatch_ReturnedFailure_DoesNotRollBackOtherWriter(bool failEntity)
    {
        await using var setup = CreateContext();
        if (failEntity)
            await setup.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectEntity BEFORE INSERT ON AuditLogs
                BEGIN SELECT RAISE(ABORT, 'Injected entity failure'); END;
                """);

        await using var provider = CreateProvider();
        await using var eventContext = CreateContext();
        var entity = EntityEnvelope();
        var explicitEvent = EventEnvelope();
        IAuditEventBatchWriter writer = new AuditEventBatchWriter(
            CreateLogger(eventContext), NullLogger<AuditEventBatchWriter>.Instance);
        if (!failEntity)
        {
            var failedWriter = new Mock<IAuditEventBatchWriter>();
            failedWriter.Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { WriteOutcome.Failed(explicitEvent.EnvelopeId, "Injected event outcome", true) });
            writer = failedWriter.Object;
        }
        var sink = CreateSink(provider, writer);

        // Reverse input kinds to verify ID correlation is independent of writer execution order.
        var error = await Assert.ThrowsAsync<AuditWriteException>(() => sink.PublishBatchAsync([explicitEvent, entity]));

        Assert.Multiple(() =>
        {
            Assert.That(error!.TotalCount, Is.EqualTo(2));
            Assert.That(error.FailedCount, Is.EqualTo(1));
            Assert.That(error.Kind, Is.Null);
            Assert.That(error.FailedEnvelopeIds, Is.EqualTo(new[] { failEntity ? entity.EnvelopeId : explicitEvent.EnvelopeId }));
        });
        await using var verify = CreateContext();
        Assert.That(await verify.AuditLogs.CountAsync(), Is.EqualTo(failEntity ? 0 : 1));
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(failEntity ? 1 : 0));
        if (!failEntity)
        {
            Assert.That((await verify.AuditLogs.SingleAsync()).EnvelopeId, Is.EqualTo(entity.EnvelopeId));
            // The failed subset can be retried without recreating the successful envelope.
            await CreateSink(provider, new AuditEventBatchWriter(CreateLogger(eventContext), NullLogger<AuditEventBatchWriter>.Instance))
                .PublishBatchAsync([explicitEvent]);
            Assert.That(await verify.AuditLogs.CountAsync(), Is.EqualTo(1));
            Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MixedBatch_InterruptedWriter_LeavesEarlierCommitWithoutFailureIds(bool cancel)
    {
        await using var provider = CreateProvider();
        using var cts = new CancellationTokenSource();
        var writer = new Mock<IAuditEventBatchWriter>();
        var failure = new IOException("Injected writer interruption");
        writer.Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<AuditEnvelope> _, CancellationToken ct) =>
            {
                if (!cancel) throw failure;
                cts.Cancel();
                throw new OperationCanceledException(ct);
            });
        var entity = EntityEnvelope();
        var sink = CreateSink(provider, writer.Object);
        if (cancel)
        {
            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => sink.PublishBatchAsync([entity, EventEnvelope()], cts.Token));
            Assert.That(error!.CancellationToken, Is.EqualTo(cts.Token));
        }
        else
        {
            var error = await Assert.ThrowsAsync<IOException>(() => sink.PublishBatchAsync([entity, EventEnvelope()]));
            Assert.That(error, Is.SameAs(failure));
        }
        await using var verify = CreateContext();
        Assert.That((await verify.AuditLogs.SingleAsync()).EnvelopeId, Is.EqualTo(entity.EnvelopeId));
        Assert.That(await verify.AuditEvents.CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExplicitEventRaceFallback_InterruptedLaterWrite_LeavesEarlierEventCommitted(bool cancel)
    {
        var replay = EventEnvelope();
        var racer = EventEnvelope();
        var successful = EventEnvelope();
        var interrupted = EventEnvelope() with { EventType = "Test.Interrupt" };
        using var cts = new CancellationTokenSource();
        var interceptor = new EventRaceInterceptor(async () =>
        {
            await using var competitor = CreateContext();
            competitor.AuditEvents.Add(StoredEvent(replay.ExplicitEventId!.Value));
            competitor.Set<AuditEnvelopeIdentity>().Add(Receipt(replay));
            await competitor.SaveChangesAsync();
        }, async () =>
        {
            // A competing commit after the existing-ID query forces the second duplicate catch.
            await using var competitor = CreateContext();
            competitor.AuditEvents.Add(StoredEvent(racer.ExplicitEventId!.Value));
            competitor.Set<AuditEnvelopeIdentity>().Add(Receipt(racer));
            await competitor.SaveChangesAsync();
        }, async () =>
        {
            if (cancel)
            {
                cts.Cancel();
                cts.Token.ThrowIfCancellationRequested();
            }
            await using var fault = CreateContext();
            await fault.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER RejectLastEvent BEFORE INSERT ON AuditEvents
                WHEN NEW.EventType = 'Test.Interrupt'
                BEGIN SELECT RAISE(ABORT, 'Injected late event failure'); END;
                """);
        });
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(Connection).AddInterceptors(interceptor).Options);
        var eventWriter = new AuditEventBatchWriter(CreateLogger(context), NullLogger<AuditEventBatchWriter>.Instance);
        var entityWriter = new Mock<IAuditEntityBatchWriter>(MockBehavior.Strict);
        var sink = new ImmediateSink(entityWriter.Object, eventWriter, NullLogger<ImmediateSink>.Instance, MillWorks.AuditCore.Tests.Helpers.TestAuditWriteAuthorization.Instance);
        AuditEnvelope[] inputs = [replay, racer, successful, interrupted];

        if (cancel)
            await Assert.ThrowsAsync<OperationCanceledException>(() => sink.PublishBatchAsync(inputs, cts.Token));
        else
            await Assert.ThrowsAsync<DbUpdateException>(() => sink.PublishBatchAsync(inputs));

        Assert.That(interceptor.Attempts, Has.Count.EqualTo(4));
        Assert.That(interceptor.Attempts[0], Is.EquivalentTo(inputs.Select(e => e.EnvelopeId)));
        Assert.That(interceptor.Attempts[1], Is.EquivalentTo(inputs.Skip(1).Select(e => e.EnvelopeId)));
        Assert.That(interceptor.Attempts.Skip(2).Select(a => a.Single()),
            Is.EqualTo(new[] { successful.EnvelopeId, interrupted.EnvelopeId }));
        await using var verify = CreateContext();
        Assert.That(await verify.AuditEvents.Select(e => e.EventId).ToListAsync(),
            Is.EquivalentTo(new[] { replay.EnvelopeId, racer.EnvelopeId, successful.EnvelopeId }));
        entityWriter.VerifyNoOtherCalls();
        await using var retry = CreateContext();
        await retry.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS RejectLastEvent");
        var replayed = await new AuditEventBatchWriter(CreateLogger(retry), NullLogger<AuditEventBatchWriter>.Instance)
            .WriteBatchAsync(inputs, default);
        Assert.That(replayed.Where(o => o.IsDuplicate).Select(o => o.EnvelopeId),
            Is.EquivalentTo(new[] { replay.EnvelopeId, racer.EnvelopeId, successful.EnvelopeId }));
        Assert.That(replayed.Single(o => o.EnvelopeId == interrupted.EnvelopeId).IsDuplicate, Is.False);
        Assert.That(await retry.AuditEvents.CountAsync(), Is.EqualTo(4));
    }

    [Test]
    public async Task TwoConcurrentInsertFallbacksPreserveEachObservedClassification()
    {
        var inputs = Enumerable.Range(0, 4).Select(_ => EventEnvelope()).ToArray();
        async Task Compete(AuditEnvelope envelope)
        {
            await using var other = CreateContext();
            other.AuditEvents.Add(StoredEvent(envelope.ExplicitEventId!.Value));
            other.Set<AuditEnvelopeIdentity>().Add(Receipt(envelope));
            await other.SaveChangesAsync();
        }
        var interceptor = new EventRaceInterceptor(() => Compete(inputs[0]), () => Compete(inputs[1]), () => Task.CompletedTask);
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(Connection).AddInterceptors(interceptor).Options);
        var outcomes = await new AuditEventBatchWriter(CreateLogger(context), NullLogger<AuditEventBatchWriter>.Instance)
            .WriteBatchAsync(inputs, default);
        Assert.That(outcomes.Where(o => o.IsDuplicate).Select(o => o.EnvelopeId),
            Is.EquivalentTo(inputs.Take(2).Select(e => e.EnvelopeId)));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);
        Assert.That(await context.AuditEvents.CountAsync(), Is.EqualTo(4));
    }

    private ServiceProvider CreateProvider()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(Connection));
        return services.BuildServiceProvider();
    }

    private static ImmediateSink CreateSink(ServiceProvider provider, IAuditEventBatchWriter events) => new(
        new AuditEntityBatchWriter(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditEntityBatchWriter>.Instance),
        events, NullLogger<ImmediateSink>.Instance, MillWorks.AuditCore.Tests.Helpers.TestAuditWriteAuthorization.Instance);

    private static AuditLogger CreateLogger(AuditDbContext context) => new(
        NullLogger<AuditLogger>.Instance, Mock.Of<IAuditEventFactory>(), new AuditEventRepository(context),
        context, Mock.Of<IAuditContext>(), new PassThroughAuditFieldRedactor(),
        securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        { EnableTamperDetection = false, EnableBatchedIntegrityWrites = false }));

    private static AuditEnvelope EntityEnvelope() => new()
    { Kind = AuditEnvelopeKind.EntityChange, EntityName = "Patient", Action = AuditAction.Updated };

    private static AuditEnvelope EventEnvelope()
    {
        var id = Guid.NewGuid();
        return new AuditEnvelope
        { EnvelopeId = id, ExplicitEventId = id, Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Patient", EventType = "Test.Event", Action = AuditAction.Updated };
    }

    private static AuditEnvelopeIdentity Receipt(AuditEnvelope e) => new()
    {
        Channel = "event", LogicalId = e.ExplicitEventId!.Value, Kind = e.Kind,
        ContentVersion = MillWorks.AuditCore.Services.Sinks.Identity.AuditEnvelopeContent.Version,
        Fingerprint = MillWorks.AuditCore.Services.Sinks.Identity.AuditEnvelopeContent.Fingerprint(e),
        AcceptedAt = DateTimeOffset.UtcNow
    };

    private static AuditEventEntity StoredEvent(Guid id) => new()
    { EventId = id, EventType = "Test.Event", JsonData = "{}", InsertedDate = DateTimeOffset.UtcNow, LastUpdatedDate = DateTimeOffset.UtcNow };

    private sealed class EventRaceInterceptor(Func<Task> firstRace, Func<Task> race, Func<Task> interrupt) : SaveChangesInterceptor
    {
        public List<Guid[]> Attempts { get; } = [];

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var ids = eventData.Context!.ChangeTracker.Entries<AuditEventEntity>()
                .Where(e => e.State == EntityState.Added).Select(e => e.Entity.EventId).ToArray();
            Attempts.Add(ids);
            if (Attempts.Count == 1) await firstRace();
            if (Attempts.Count == 2) await race();
            if (Attempts.Count == 4) await interrupt();
            return result;
        }
    }
}
