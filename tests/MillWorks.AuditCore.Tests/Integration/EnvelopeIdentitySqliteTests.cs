using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Identity;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Services.Telemetry;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration;

[TestFixture]
public sealed partial class EnvelopeIdentitySqliteTests : SqliteIntegrationFixture
{
    [TearDown]
    public async Task ClearOutbox()
    {
        await using var context = CreateContext();
        await context.AuditOutboxFailureHandoffs.ExecuteDeleteAsync();
        await context.Database.ExecuteSqlRawAsync("DELETE FROM AuditOutboxResolutions; DELETE FROM AuditOutboxOperations;");
        await context.AuditOutbox.ExecuteDeleteAsync();
    }

    private static AuditEnvelope Event() => new()
    {
        Kind = AuditEnvelopeKind.ExplicitEvent, Action = AuditAction.Updated, EntityName = "Invoice",
        ExplicitEventId = Guid.NewGuid(), EventType = "Invoice.Updated", UserId = "actor-1",
        GovernanceIdentity = new(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        OccurredAt = DateTimeOffset.Parse("2026-10-16T12:00:00Z"), AdditionalData = "{\"a\":1,\"b\":2}"
    };

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReEnvelopingPreservesOneLogicalIdentity(bool outbox)
    {
        var original = Event();
        await Publish(outbox, original);
        await Publish(outbox, original with { EnvelopeId = Guid.NewGuid(), AdditionalData = "{\"b\":2,\"a\":1.0}" });
        await using var verify = CreateContext();
        if (outbox)
        {
            var row = await verify.AuditOutbox.SingleAsync();
            Assert.That(row.IdempotencyKey, Is.EqualTo(original.ExplicitEventId));
            var hydrated = QueuedEnvelopeValidator.Read(row.EnvelopeJson, row.EnvelopeVersion);
            Assert.That(hydrated.ExplicitEventId, Is.EqualTo(original.ExplicitEventId));
            Assert.That(hydrated.EnvelopeId, Is.EqualTo(original.EnvelopeId));
        }
        else Assert.That((await verify.AuditEvents.SingleAsync()).EventId, Is.EqualTo(original.ExplicitEventId));
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false, "payload")]
    [TestCase(true, "payload")]
    [TestCase(false, "actor")]
    [TestCase(true, "actor")]
    [TestCase(false, "tenant")]
    [TestCase(true, "tenant")]
    public async Task ChangedContentIsConflictAndOriginalRemainsUntouched(bool outbox, string change)
    {
        var original = Event();
        await Publish(outbox, original);
        var changed = original with { EnvelopeId = Guid.NewGuid() };
        changed = change switch
        {
            "payload" => changed with { AdditionalData = "PRIVATE-CHANGED-CONTENT" },
            "actor" => changed with { UserId = "PRIVATE-CHANGED-ACTOR" },
            _ => changed with { GovernanceIdentity = new(Guid.NewGuid()) }
        };
        var error = await Assert.CatchAsync<Exception>(() => Publish(outbox, changed));
        var conflict = outbox ? error : error!.InnerException;
        Assert.That(conflict, Is.TypeOf<AuditIdentityConflictException>());
        Assert.That(conflict!.Message, Is.EqualTo("audit_identity_conflict"));
        Assert.That(conflict.Message, Does.Not.Contain("PRIVATE"));
        await using var verify = CreateContext();
        var receipt = await verify.Set<AuditEnvelopeIdentity>().SingleAsync();
        Assert.That(receipt.Fingerprint, Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
        Assert.That(receipt.TenantId, Is.EqualTo(original.GovernanceIdentity!.TenantId));
        Assert.That(outbox ? await verify.AuditOutbox.CountAsync() : await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task SameBatchReferencesOnlyBecomeDuplicatesAfterCandidateSucceeds()
    {
        var original = Event();
        var reference = original with { EnvelopeId = Guid.NewGuid() };
        var conflicting = original with { EnvelopeId = Guid.NewGuid(), UserId = "different" };
        await using var context = CreateContext();
        var outcomes = await Writer(context).WriteBatchAsync([original, reference, conflicting], default);
        Assert.Multiple(() =>
        {
            Assert.That(outcomes.Select(o => o.EnvelopeId), Is.EqualTo(new[] { original.EnvelopeId, reference.EnvelopeId, conflicting.EnvelopeId }));
            Assert.That(outcomes[0].Succeeded, Is.True);
            Assert.That(outcomes[1].IsDuplicate, Is.True);
            Assert.That(outcomes[2].IsConflict, Is.True);
            Assert.That(outcomes[2].IsRetryable, Is.False);
        });
        Assert.That(await context.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task AFailedCandidateCannotMakeItsAliasesSuccessful()
    {
        await using var setup = CreateContext();
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER RejectEvent BEFORE INSERT ON AuditEvents
            BEGIN SELECT RAISE(ABORT, 'Injected event failure'); END;
            """);
        try
        {
            var original = Event();
            await Assert.ThrowsAsync<DbUpdateException>(() => Writer(setup).WriteBatchAsync(
                [original, original with { EnvelopeId = Guid.NewGuid() }], default));
            await using var verify = CreateContext();
            Assert.That(await verify.AuditEvents.CountAsync(), Is.Zero);
            Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.Zero);
        }
        finally { await setup.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectEvent"); }
    }

    [Test]
    public async Task NewLogicalIdsWithIdenticalContentRemainDistinct()
    {
        var original = Event();
        await Publish(false, original);
        await Publish(false, original with { EnvelopeId = Guid.NewGuid(), ExplicitEventId = Guid.NewGuid() });
        await using var verify = CreateContext();
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task OldRowsWithoutComparisonEvidenceAreNotAcknowledgedAsDuplicates(bool outbox)
    {
        var original = Event();
        await using (var seed = CreateContext())
        {
            if (outbox) seed.AuditOutbox.Add(new AuditOutboxEntity
            { EnvelopeJson = JsonSerializer.Serialize(original, QueuedEnvelopeValidator.Json), IdempotencyKey = original.ExplicitEventId!.Value });
            else seed.AuditEvents.Add(new AuditEventEntity { EventId = original.ExplicitEventId!.Value, JsonData = "{}" });
            await seed.SaveChangesAsync();
        }
        var error = await Assert.CatchAsync<Exception>(() => Publish(outbox, original));
        Assert.That((outbox ? error : error!.InnerException)?.Message, Is.EqualTo("audit_identity_unverified"));
        await using var verify = CreateContext();
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.Zero);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RetainedIdentityCannotRecreateDisposedPayload(bool outbox)
    {
        var original = Event();
        await Publish(outbox, original);
        await using (var cleanup = CreateContext())
        {
            if (outbox) await cleanup.AuditOutbox.ExecuteDeleteAsync();
            else await cleanup.AuditEvents.ExecuteDeleteAsync();
        }
        var error = await Assert.CatchAsync<Exception>(() => Publish(outbox, original));
        Assert.That((outbox ? error : error!.InnerException)?.Message, Is.EqualTo("audit_identity_unverified"));
        await using var verify = CreateContext();
        Assert.That(outbox ? await verify.AuditOutbox.CountAsync() : await verify.AuditEvents.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task QueuedIdentityAndContentRemainStableAfterDrain()
    {
        var original = Event();
        await Publish(true, original);
        await using (var drain = CreateContext())
        {
            var queued = await drain.AuditOutbox.SingleAsync();
            var hydrated = QueuedEnvelopeValidator.Read(queued.EnvelopeJson, queued.EnvelopeVersion);
            await Writer(drain).WriteBatchAsync([hydrated], default);
            queued.Status = AuditOutboxStatus.Completed;
            await drain.SaveChangesAsync();
        }
        await Publish(true, original with { EnvelopeId = Guid.NewGuid() });
        await Publish(false, original with { EnvelopeId = Guid.NewGuid() });
        await Assert.ThrowsAsync<AuditIdentityConflictException>(() => Publish(true,
            original with { EnvelopeId = Guid.NewGuid(), AdditionalData = "changed after drain" }));
        await using var verify = CreateContext();
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That(await verify.AuditOutbox.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task OrdinaryDlqSerializationPreservesOriginalEnvelopeIdentity()
    {
        var original = Event();
        var mapped = AuditEventBatchWriter.MapToAuditEvent(original);
        var restored = JsonSerializer.Deserialize<AuditEvent>(JsonSerializer.Serialize(mapped))!;
        await using var context = CreateContext();
        await Logger(context).LogAsync(restored);
        await Publish(false, original with { EnvelopeId = Guid.NewGuid() });
        Assert.That(await context.AuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That((await context.Set<AuditEnvelopeIdentity>().SingleAsync()).Fingerprint,
            Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task MissingPublicationAuthorityDeniesBeforePersistence(bool outbox)
    {
        await Assert.ThrowsAsync<AuditPublicationAuthorizationException>(() => Publish(outbox, Event(), authorized: false));
        await using var verify = CreateContext();
        Assert.That(await verify.AuditEvents.CountAsync(), Is.Zero);
        Assert.That(await verify.AuditOutbox.CountAsync(), Is.Zero);
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task DownstreamMaskingCannotHideChangedOriginalInput()
    {
        var original = Event();
        await using (var db = CreateContext())
        {
            var maskedLogger = new AuditLogger(NullLogger<AuditLogger>.Instance, Mock.Of<IAuditEventFactory>(),
                new AuditEventRepository(db), db, Mock.Of<IAuditContext>(), new MaskAllFields(),
                securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = false }));
            await new AuditEventBatchWriter(maskedLogger, NullLogger<AuditEventBatchWriter>.Instance).WriteBatchAsync([original], default);
            Assert.That((await db.AuditEvents.SingleAsync()).JsonData, Does.Not.Contain("actor-1"));
        }
        await using var retry = CreateContext();
        var outcomes = await Writer(retry).WriteBatchAsync([original with { EnvelopeId = Guid.NewGuid(), UserId = "actor-2" }], default);
        Assert.That(outcomes.Single().IsConflict, Is.True);
        Assert.That(await retry.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task LostSaveAcknowledgementReplaysAgainstDurableReceipt()
    {
        var original = Event();
        var fault = new LostAcknowledgement();
        await using (var db = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
                         .UseSqlite(Connection).AddInterceptors(fault).Options))
            await Assert.ThrowsAsync<IOException>(() => Writer(db).WriteBatchAsync([original], default));
        await Publish(false, original with { EnvelopeId = Guid.NewGuid() });
        await using var verify = CreateContext();
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task RevokedAuthorityPreventsAnyReplayOrConflictDisclosure(bool outbox)
    {
        var original = Event();
        await Publish(outbox, original);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Publish(outbox,
            original with { EnvelopeId = Guid.NewGuid(), UserId = "changed" }, policy: new DenyPublication()));
        await using var verify = CreateContext();
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task SingleEventRaceLeavesNoFailedInsertsInTheScopedTracker()
    {
        var original = Event();
        var race = new BeforeFirstSave(async () => await Publish(false, original));
        await using var contender = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(Connection).AddInterceptors(race).Options);
        var logger = Logger(contender);
        await logger.LogAsync(AuditEventBatchWriter.MapToAuditEvent(original with { EnvelopeId = Guid.NewGuid() }));
        Assert.That(contender.ChangeTracker.Entries().Any(e => e.State == EntityState.Added), Is.False);
        await logger.LogAsync(AuditEventBatchWriter.MapToAuditEvent(Event()));
        Assert.That(await contender.AuditEvents.CountAsync(), Is.EqualTo(2));
        Assert.That(await contender.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(2));
    }

    private sealed class BeforeFirstSave(Func<Task> before) : SaveChangesInterceptor
    {
        private bool fired;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!fired) { fired = true; await before(); }
            return result;
        }
    }

    [Test]
    public async Task LegacyLoggerCannotBypassOwnedEnvelopeIdentity()
    {
        var original = Event();
        await Publish(false, original);
        await using var db = CreateContext();
        var error = await Assert.ThrowsAsync<AuditIdentityConflictException>(() => Logger(db).LogAsync(
            new AuditEvent { EventId = original.ExplicitEventId!.Value, EventType = "changed" }));
        Assert.That(error!.Unverified, Is.True);
        Assert.That(await db.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task LoggerWithoutTheIdentityCapabilityCannotFabricateAcceptance()
    {
        var writer = new AuditEventBatchWriter(Mock.Of<MillWorks.AuditCore.Services.Interfaces.IAuditLogger>(), NullLogger<AuditEventBatchWriter>.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.WriteBatchAsync([Event()], default));
    }

    private sealed class MaskAllFields : IAuditFieldRedactor
    {
        public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields) => new();
        public string? RedactValue(string name, string? value) => value is null ? null : "MASKED";
    }
    private sealed class DenyPublication : IAuditEnvelopeWriteAuthorization
    {
        public ValueTask<IAuditEnvelopeWritePermit> AcquireAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken ct)
            => throw new UnauthorizedAccessException("Denied by current host policy.");
    }
    private sealed class LostAcknowledgement : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
            => throw new IOException("Injected after durable SaveChanges commit.");
    }

    [Test]
    public async Task DrainerRetainsCrossStoreConflictWithAStableReasonCode()
    {
        var original = Event();
        await Publish(false, original);
        // Consumer acceptance is provisional; the owner may discover an event-store conflict later.
        await Publish(true, original with { EnvelopeId = Guid.NewGuid(), AdditionalData = "conflicting queued input" });
        var options = new SecurityOptions
        {
            AuditSinkMode = AuditSinkMode.TransactionalOutbox, OutboxDrainerPollInterval = TimeSpan.FromMilliseconds(10),
            EnableTamperDetection = false
        };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(Connection));
        services.AddSingleton<OutboxStateStore>();
        services.AddScoped<OutboxHandoffDispatcher>();
        services.AddScoped<MillWorks.AuditCore.Services.Interfaces.IAuditLogger>(sp => Logger(sp.GetRequiredService<AuditDbContext>()));
        services.AddScoped<IAuditEntityBatchWriter, AuditEntityBatchWriter>();
        services.AddScoped<IAuditEventBatchWriter, AuditEventBatchWriter>();
        services.AddScoped<IAuditBatchProcessor, AuditBatchProcessor>();
        await using var provider = services.BuildServiceProvider();
        using var observer = new AuditOutboxQueueObserver();
        using var drainer = new AuditOutboxDrainer(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance, Microsoft.Extensions.Options.Options.Create(options), observer);
        await using (var scope = provider.CreateAsyncScope())
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Assert.That(await drainer.DrainBatchAsync(scope.ServiceProvider, timeout.Token), Is.EqualTo(1));
        }
        await using var verify = CreateContext();
        Assert.That((await verify.AuditOutbox.SingleAsync()).LastError, Is.EqualTo("audit_identity_conflict"));
        Assert.That((await verify.AuditOutboxFailureHandoffs.SingleAsync()).ReasonCode, Is.EqualTo("audit_identity_conflict"));
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task LegacyOutboxWriterIsRejectedBeforeItCanClaimAcceptance()
    {
        var sink = new TransactionalOutboxSink(Mock.Of<IAuditOutboxWriter>(), NullLogger<TransactionalOutboxSink>.Instance,
            TestAuditWriteAuthorization.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => sink.PublishAsync(Event()));
    }

    private async Task Publish(bool outbox, AuditEnvelope envelope, bool authorized = true, IAuditEnvelopeWriteAuthorization? policy = null)
    {
        await using var context = CreateContext();
        IAuditEnvelopeWriteAuthorization? grant = authorized ? policy ?? TestAuditWriteAuthorization.Instance : null;
        if (outbox)
        {
            var accessor = new ConsumerDbContextAccessor();
            var writer = new AuditOutboxWriter(accessor, Microsoft.Extensions.Options.Options.Create(new EntityFrameworkOptions()), NullLogger<AuditOutboxWriter>.Instance);
            var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance, grant);
            using (accessor.SetCurrent(context)) await sink.PublishAsync(envelope);
            await context.SaveChangesAsync();
        }
        else
        {
            await using var provider = new ServiceCollection().AddDbContext<AuditDbContext>(o => o.UseSqlite(Connection)).BuildServiceProvider();
            var sink = new ImmediateSink(new AuditEntityBatchWriter(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditEntityBatchWriter>.Instance),
                Writer(context), NullLogger<ImmediateSink>.Instance, grant);
            await sink.PublishAsync(envelope);
        }
    }

    private static AuditEventBatchWriter Writer(AuditDbContext context) => new(Logger(context), NullLogger<AuditEventBatchWriter>.Instance);
    private static AuditLogger Logger(AuditDbContext context) => new(
        NullLogger<AuditLogger>.Instance, Mock.Of<IAuditEventFactory>(), new AuditEventRepository(context), context,
        Mock.Of<IAuditContext>(), new PassThroughAuditFieldRedactor(),
        securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = false }));
}
