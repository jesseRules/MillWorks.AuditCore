using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
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
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Identity;

[TestFixture(false)]
[TestFixture(true)]
[Category("SqlServer")]
public sealed partial class EnvelopeIdentitySqlServerTests(bool rcsi)
{
    private readonly string _database = "AuditCoreH3a_" + Guid.NewGuid().ToString("N");
    private string _connection = null!;
    private string _master = null!;
    private SaveGate? _gate;
    private bool _created;
    private bool _upgradePreservedLegacyRow;

    [OneTimeSetUp]
    public async Task CreateDisposableDatabase()
    {
        if (Environment.GetEnvironmentVariable("MILLWORKS_SQL_TESTS_ENABLED") != "1")
            Assert.Ignore("Enable disposable SQL tests on localhost:1435.");
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "127.0.0.1,1435", InitialCatalog = "master", UserID = "sa",
            Password = Environment.GetEnvironmentVariable("MILLWORKS_SQL_TEST_PASSWORD") ?? "Dev@12345678",
            TrustServerCertificate = true, ConnectTimeout = 5
        };
        _master = builder.ConnectionString;
        await using (var connection = new SqlConnection(_master))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{_database}]; ALTER DATABASE [{_database}] SET READ_COMMITTED_SNAPSHOT {(rcsi ? "ON" : "OFF")};";
            await command.ExecuteNonQueryAsync();
            _created = true;
        }
        builder.InitialCatalog = _database;
        _connection = builder.ConnectionString;
        await using var db = Context();
        await db.GetService<IMigrator>().MigrateAsync("20261016211640_RetainOutboxFailureSources");
        var id = Guid.NewGuid();
        db.AuditOutbox.Add(new AuditOutboxEntity { Id = id, EnvelopeJson = "{\"legacy\":true}", EnvelopeVersion = 1, IdempotencyKey = id });
        await db.SaveChangesAsync();
        await db.Database.MigrateAsync();
        _upgradePreservedLegacyRow = (await db.AuditOutbox.AsNoTracking().SingleAsync()).EnvelopeJson == "{\"legacy\":true}" &&
            await db.Set<AuditEnvelopeIdentity>().CountAsync() == 0;
    }

    [SetUp]
    public async Task Reset()
    {
        _gate = null;
        await using var db = Context();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM [audit].[AuditEnvelopeIdentities]; DELETE FROM [audit].[AuditOutbox]; DELETE FROM [audit].[AuditEvents]; DELETE FROM [audit].[AuditLogSubjectLinks]; DELETE FROM [audit].[AuditLogs];");
    }

    [OneTimeTearDown]
    public async Task DropDisposableDatabase()
    {
        if (!_created) return;
        SqlConnection.ClearAllPools();
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];";
        await command.ExecuteNonQueryAsync();
    }

    [Test]
    public void IncrementalMigrationDoesNotInventHistoricalComparisonEvidence() => Assert.That(_upgradePreservedLegacyRow, Is.True);

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReEnvelopeReplayAndChangedContentUseTheSameProtocol(bool raw)
    {
        var original = Event();
        await Outbox(raw, [original]);
        await Outbox(raw, [original with { EnvelopeId = Guid.NewGuid() }]);
        var changed = original with { EnvelopeId = Guid.NewGuid(), AdditionalData = "changed" };
        await Assert.ThrowsAsync<AuditIdentityConflictException>(() => Outbox(raw, [changed]));
        await using var db = Context();
        Assert.That(await db.AuditOutbox.CountAsync(), Is.EqualTo(1));
        Assert.That((await db.Set<AuditEnvelopeIdentity>().SingleAsync()).Fingerprint, Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConsumerRollbackRemovesSourceAndIdentity(bool raw)
    {
        await Outbox(raw, [Event()], rollback: true);
        await using var db = Context();
        Assert.That(await db.AuditOutbox.CountAsync(), Is.Zero);
        Assert.That(await db.Set<AuditEnvelopeIdentity>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task ConflictInLaterRawChunkRollsBackTheEntirePublicationCall()
    {
        var original = Event();
        await Outbox(true, [original]);
        var inputs = Enumerable.Range(0, 128).Select(_ => Event()).Append(
            original with { EnvelopeId = Guid.NewGuid(), AdditionalData = "changed" }).ToArray();
        var earlier = Event();
        await using (var consumer = new BareContext(new DbContextOptionsBuilder<BareContext>().UseSqlServer(_connection).Options))
        {
            await using var transaction = await consumer.Database.BeginTransactionAsync();
            var accessor = new ConsumerDbContextAccessor();
            var writer = new AuditOutboxWriter(accessor, Microsoft.Extensions.Options.Options.Create(new EntityFrameworkOptions()),
                NullLogger<AuditOutboxWriter>.Instance);
            var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance,
                TestAuditWriteAuthorization.Instance);
            using (accessor.SetCurrent(consumer))
            {
                await sink.PublishAsync(earlier);
                await Assert.ThrowsAsync<AuditIdentityConflictException>(() => sink.PublishBatchAsync(inputs));
            }
            // Commit after the handled conflict: only this call's staged chunks must be
            // rolled back, while the earlier caller transaction work must survive.
            await transaction.CommitAsync();
        }
        await using var db = Context();
        Assert.That(await db.AuditOutbox.CountAsync(), Is.EqualTo(2));
        Assert.That(await db.AuditOutbox.AnyAsync(x => x.IdempotencyKey == earlier.ExplicitEventId), Is.True);
        Assert.That(await db.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(2));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task ConcurrentOutboxContendersNeverAcknowledgeChangedContent(bool raw, bool changed)
    {
        if (!raw) _gate = new SaveGate();
        var original = Event();
        var second = original with { EnvelopeId = Guid.NewGuid(), AdditionalData = changed ? "different" : original.AdditionalData };
        var errors = await Task.WhenAll(Capture(() => Outbox(raw, [original])), Capture(() => Outbox(raw, [second])));
        Assert.That(errors.Count(e => e is null), Is.EqualTo(changed ? 1 : 2));
        if (changed) Assert.That(errors.Single(e => e is not null), Is.TypeOf<AuditIdentityConflictException>());
        await using var db = Context();
        Assert.That(await db.AuditOutbox.CountAsync(), Is.EqualTo(1));
        Assert.That(await db.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ConcurrentImmediateContendersKeepTheWinningLogicalEvent(bool changed)
    {
        _gate = new SaveGate();
        var original = Event();
        var second = original with { EnvelopeId = Guid.NewGuid(), UserId = changed ? "other actor" : original.UserId };
        var errors = await Task.WhenAll(Capture(() => Immediate(original)), Capture(() => Immediate(second)));
        Assert.That(errors.Count(e => e is null), Is.EqualTo(changed ? 1 : 2));
        if (changed) Assert.That(errors.Single(e => e is not null)!.InnerException, Is.TypeOf<AuditIdentityConflictException>());
        await using var db = Context();
        Assert.That((await db.AuditEvents.SingleAsync()).EventId, Is.EqualTo(original.ExplicitEventId));
        Assert.That(await db.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task EntityRepairProtectsTheOriginalReceiptAndBlocksOverlappingCleanup()
    {
        var original = Event() with
        {
            Kind = AuditEnvelopeKind.EntityChange, ExplicitEventId = null, EventType = null,
            PropertyChanges = [new("Amount", "1", "2"), new("Status", "draft", "issued")]
        };
        Assert.That((await Entity(original)).Single().Succeeded, Is.True);
        await using (var setup = Context())
            await setup.AuditLogs.Where(x => x.EnvelopeId == original.EnvelopeId && x.PropertyName == "Status").ExecuteDeleteAsync();
        var repair = new BeforeSave(async () =>
        {
            // The writer has read the surviving source under SERIALIZABLE. A real
            // independent delete must time out on its retained locks before repair saves.
            await using var cleanup = new SqlConnection(_connection);
            await cleanup.OpenAsync();
            await using var command = cleanup.CreateCommand();
            command.CommandText = "SET LOCK_TIMEOUT 200; DELETE FROM [audit].[AuditLogs] WHERE EnvelopeId = @id;";
            command.Parameters.AddWithValue("@id", original.EnvelopeId);
            var error = await Assert.ThrowsAsync<SqlException>(async () => await command.ExecuteNonQueryAsync());
            Assert.That(error!.Number, Is.EqualTo(1222));
        });
        Assert.That((await Entity(original, repair)).Single().Succeeded, Is.True);
        var changed = original with { PropertyChanges = [new("Amount", "1", "99"), new("Status", "draft", "issued")] };
        Assert.That((await Entity(changed)).Single().IsConflict, Is.True);
        await using var verify = Context();
        Assert.That(await verify.AuditLogs.CountAsync(), Is.EqualTo(2));
        Assert.That((await verify.AuditLogs.SingleAsync(x => x.PropertyName == "Amount")).NewValue, Is.EqualTo("2"));
        Assert.That((await verify.Set<AuditEnvelopeIdentity>().SingleAsync()).Fingerprint,
            Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
        await verify.AuditLogs.ExecuteDeleteAsync();
        var replay = (await Entity(original)).Single();
        Assert.That(((AuditIdentityConflictException)replay.Exception!).Unverified, Is.True);
        Assert.That(await verify.AuditLogs.CountAsync(), Is.Zero);
    }

    private async Task<IReadOnlyList<WriteOutcome>> Entity(AuditEnvelope envelope, SaveChangesInterceptor? interceptor = null)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(o =>
        {
            o.UseSqlServer(_connection);
            if (interceptor is not null) o.AddInterceptors(interceptor);
        });
        await using var provider = services.BuildServiceProvider();
        return await new AuditEntityBatchWriter(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance).WriteBatchAsync([envelope], default);
    }

    private sealed class BeforeSave(Func<Task> before) : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            await before();
            return result;
        }
    }

    [Test]
    public async Task ConcurrentMixedBatchesPreserveNewAndVerifiedDuplicateOutcomes()
    {
        _gate = new SaveGate();
        var shared = Event(); var left = Event(); var right = Event();
        async Task<BatchAuditResult> Write(params AuditEnvelope[] inputs)
        {
            await using var db = Context();
            var logger = new AuditLogger(NullLogger<AuditLogger>.Instance, Mock.Of<IAuditEventFactory>(), new AuditEventRepository(db), db,
                Mock.Of<IAuditContext>(), new PassThroughAuditFieldRedactor(),
                securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = false }));
            return await logger.LogEnvelopeBatchAsync(inputs);
        }
        var results = await Task.WhenAll(Write(shared, left), Write(shared with { EnvelopeId = Guid.NewGuid() }, right));
        var outcomes = results.SelectMany(r => r.EventOutcomes!).ToArray();
        Assert.That(outcomes.Count(x => x.Status == AuditEventWriteStatus.Persisted), Is.EqualTo(3));
        Assert.That(outcomes.Count(x => x.Status == AuditEventWriteStatus.Duplicate), Is.EqualTo(1));
        Assert.That(outcomes.Single(x => x.Status == AuditEventWriteStatus.Duplicate).EventId, Is.EqualTo(shared.ExplicitEventId));
        Assert.That(outcomes.Where(x => x.EventId == left.ExplicitEventId || x.EventId == right.ExplicitEventId)
            .All(x => x.Status == AuditEventWriteStatus.Persisted), Is.True);
        await using var verify = Context();
        Assert.That(await verify.AuditEvents.CountAsync(), Is.EqualTo(3));
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(3));
    }

    [TestCase(false, false)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task CommitBetweenReceiptAndSourceReadsUsesTheWinningEvidence(bool outbox, bool changed)
    {
        var original = Event();
        var interceptor = new ReceiptReadGap(() => outbox ? Outbox(false, [original]) : Immediate(original));
        await using var contender = Context(interceptor);
        var retry = original with { EnvelopeId = Guid.NewGuid(), AdditionalData = changed ? "different input" : original.AdditionalData };
        if (outbox)
        {
            var accessor = new ConsumerDbContextAccessor();
            var writer = new AuditOutboxWriter(accessor, Microsoft.Extensions.Options.Options.Create(new EntityFrameworkOptions()),
                NullLogger<AuditOutboxWriter>.Instance);
            var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance, TestAuditWriteAuthorization.Instance);
            using (accessor.SetCurrent(contender))
            {
                if (changed)
                {
                    var conflict = await Assert.ThrowsAsync<AuditIdentityConflictException>(() => sink.PublishAsync(retry));
                    Assert.That(conflict!.Unverified, Is.False);
                }
                else await sink.PublishAsync(retry);
            }
            Assert.That(contender.ChangeTracker.Entries().Any(x => x.State == EntityState.Added), Is.False);
            await contender.SaveChangesAsync();
        }
        else
        {
            var outcomes = await new AuditEventBatchWriter(Logger(contender), NullLogger<AuditEventBatchWriter>.Instance)
                .WriteBatchAsync([retry], default);
            Assert.That(outcomes.Single().IsDuplicate, Is.EqualTo(!changed));
            Assert.That(outcomes.Single().IsConflict, Is.EqualTo(changed));
            if (changed) Assert.That(((AuditIdentityConflictException)outcomes.Single().Exception!).Unverified, Is.False);
        }
        Assert.That(interceptor.Fired, Is.True);
        await using var verify = Context();
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(1));
        Assert.That(outbox ? await verify.AuditOutbox.CountAsync() : await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
        Assert.That((await verify.Set<AuditEnvelopeIdentity>().SingleAsync()).Fingerprint, Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ExistingSourceWithoutReceiptRemainsUnverified(bool outbox)
    {
        var original = Event();
        if (outbox) await Outbox(false, [original]); else await Immediate(original);
        await using (var legacy = Context()) await legacy.Set<AuditEnvelopeIdentity>().ExecuteDeleteAsync();
        if (outbox)
        {
            var conflict = await Assert.ThrowsAsync<AuditIdentityConflictException>(() => Outbox(false, [original]));
            Assert.That(conflict!.Unverified, Is.True);
        }
        else
        {
            await using var retry = Context();
            var conflict = await Assert.ThrowsAsync<AuditIdentityConflictException>(() => Logger(retry).LogEnvelopeBatchAsync([original]));
            Assert.That(conflict!.Unverified, Is.True);
        }
        await using var verify = Context();
        Assert.That(await verify.Set<AuditEnvelopeIdentity>().CountAsync(), Is.Zero);
        Assert.That(outbox ? await verify.AuditOutbox.CountAsync() : await verify.AuditEvents.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task LateConflictPreservesEarlierFallbackCommitsAndAliases()
    {
        var inputs = Enumerable.Range(0, 5).Select(_ => Event()).ToArray();
        var calls = 0;
        var interceptor = new BeforeSave(async () =>
        {
            var attempt = ++calls;
            if (attempt > 3) return;
            var accepted = attempt == 1 ? inputs[0] : attempt == 2 ? inputs[1]
                : inputs[3] with { AdditionalData = "competing different content" };
            await Immediate(accepted);
        });
        await using var contender = Context(interceptor);
        var alias = inputs[2] with { EnvelopeId = Guid.NewGuid() };
        var outcomes = await new AuditEventBatchWriter(Logger(contender), NullLogger<AuditEventBatchWriter>.Instance)
            .WriteBatchAsync([.. inputs, alias], default);
        Assert.That(outcomes.Where(x => x.Succeeded && !x.IsDuplicate).Select(x => x.EnvelopeId),
            Is.EquivalentTo(new[] { inputs[2].EnvelopeId, inputs[4].EnvelopeId }));
        Assert.That(outcomes.Where(x => x.IsDuplicate).Select(x => x.EnvelopeId),
            Is.EquivalentTo(new[] { inputs[0].EnvelopeId, inputs[1].EnvelopeId, alias.EnvelopeId }));
        var conflict = outcomes.Single(x => x.EnvelopeId == inputs[3].EnvelopeId);
        Assert.That(conflict.IsConflict, Is.True);
        Assert.That(conflict.IsRetryable, Is.False);
        Assert.That(calls, Is.EqualTo(4)); // initial bulk, second bulk, C and E; D is a conflict
        Assert.That(await contender.AuditEvents.CountAsync(), Is.EqualTo(5));
        Assert.That(await contender.Set<AuditEnvelopeIdentity>().CountAsync(), Is.EqualTo(5));
        var replay = await new AuditEventBatchWriter(Logger(contender), NullLogger<AuditEventBatchWriter>.Instance)
            .WriteBatchAsync(inputs.Where((_, index) => index != 3).ToArray(), default);
        Assert.That(replay.All(x => x.IsDuplicate), Is.True);
    }

    private static AuditLogger Logger(AuditDbContext db) => new(NullLogger<AuditLogger>.Instance,
        Mock.Of<IAuditEventFactory>(), new AuditEventRepository(db), db, Mock.Of<IAuditContext>(), new PassThroughAuditFieldRedactor(),
        securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = false }));

    private sealed class ReceiptReadGap(Func<Task> competitor) : DbCommandInterceptor
    {
        internal bool Fired { get; private set; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData data,
            DbDataReader result, CancellationToken ct = default)
        {
            if (!Fired && command.CommandText.Contains("FROM [audit].[AuditEnvelopeIdentities]", StringComparison.Ordinal))
            {
                Fired = true;
                await competitor();
            }
            return result;
        }
    }

    private async Task Outbox(bool raw, IReadOnlyList<AuditEnvelope> envelopes, bool rollback = false)
    {
        // Mapped acceptance precedes SaveChanges. A uniqueness/deadlock race aborts that
        // consumer attempt; retry the WHOLE transaction, never acknowledge a failed save.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using DbContext consumer = raw ? new BareContext(new DbContextOptionsBuilder<BareContext>().UseSqlServer(_connection).Options) : Context();
                await using var transaction = await consumer.Database.BeginTransactionAsync();
                var accessor = new ConsumerDbContextAccessor();
                var writer = new AuditOutboxWriter(accessor, Microsoft.Extensions.Options.Options.Create(new EntityFrameworkOptions()), NullLogger<AuditOutboxWriter>.Instance);
                var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance, TestAuditWriteAuthorization.Instance);
                using (accessor.SetCurrent(consumer)) await sink.PublishBatchAsync(envelopes);
                if (!raw) await consumer.SaveChangesAsync();
                if (rollback) await transaction.RollbackAsync(); else await transaction.CommitAsync();
                return;
            }
            catch (DbUpdateException) when (attempt < 3) { }
            catch (SqlException e) when (e.Number == 1205 && attempt < 3) { }
        }
    }

    private async Task Immediate(AuditEnvelope envelope)
    {
        await using var db = Context();
        await using var provider = new ServiceCollection().AddDbContext<AuditDbContext>(o => o.UseSqlServer(_connection)).BuildServiceProvider();
        var logger = new AuditLogger(NullLogger<AuditLogger>.Instance, Mock.Of<IAuditEventFactory>(), new AuditEventRepository(db), db,
            Mock.Of<IAuditContext>(), new PassThroughAuditFieldRedactor(),
            securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = false }));
        var sink = new ImmediateSink(new AuditEntityBatchWriter(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditEntityBatchWriter>.Instance),
            new AuditEventBatchWriter(logger, NullLogger<AuditEventBatchWriter>.Instance), NullLogger<ImmediateSink>.Instance, TestAuditWriteAuthorization.Instance);
        await sink.PublishAsync(envelope);
    }

    private static async Task<Exception?> Capture(Func<Task> work)
    {
        try { await work(); return null; }
        catch (Exception error) { return error; }
    }
    private AuditDbContext Context(IInterceptor? interceptor = null)
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlServer(_connection);
        if (_gate is not null) options.AddInterceptors(_gate);
        if (interceptor is not null) options.AddInterceptors(interceptor);
        return new AuditDbContext(options.Options);
    }
    private sealed class SaveGate : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _arrived) >= 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
            return result;
        }
    }
    private static AuditEnvelope Event() => new()
    {
        Kind = AuditEnvelopeKind.ExplicitEvent, Action = AuditAction.Updated, EntityName = "Invoice",
        ExplicitEventId = Guid.NewGuid(), EventType = "Invoice.Updated", UserId = "actor",
        GovernanceIdentity = new(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        OccurredAt = DateTimeOffset.Parse("2026-10-16T12:00:00Z"), AdditionalData = "{\"value\":1}"
    };
    private sealed class BareContext(DbContextOptions<BareContext> options) : DbContext(options) { }
}
