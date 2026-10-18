using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Tests.Sinks;

[TestFixture("sqlite", false)]
[TestFixture("sql", false)]
[TestFixture("sql", true)]
[NonParallelizable]
public sealed partial class OutboxOwnershipTests(string provider, bool rcsi)
{
    private ServiceProvider _services = null!;
    private DbContextOptions<AuditDbContext> _options = null!;
    private OutboxStateStore _store = null!;
    private SqliteConnection? _keeper;
    private string? _database;
    private string? _master;
    private readonly CommitFault _fault = new();
    private readonly CommandCounter _commands = new();
    private readonly TestAuthorization _authorization = new();
    private readonly Mock<IAuditBatchProcessor> _processor = new();
    private readonly Mock<IAuditDeadLetterQueue> _dlq = new();
    private Guid _tenant;

    [OneTimeSetUp]
    public async Task InitializeFixture()
    {
        var builder = new DbContextOptionsBuilder<AuditDbContext>();
        if (provider == "sqlite")
        {
            var connection = $"Data Source=OutboxH1_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
            _keeper = new SqliteConnection(connection);
            await _keeper.OpenAsync();
            builder.UseSqlite(connection);
        }
        else
        {
            if (Environment.GetEnvironmentVariable("MILLWORKS_SQL_TESTS_ENABLED") != "1")
                Assert.Ignore("Enable disposable SQL tests on port 1435.");
            _database = $"AuditCoreOutboxH1_{Guid.NewGuid():N}";
            var connection = new SqlConnectionStringBuilder
            {
                DataSource = "127.0.0.1,1435", InitialCatalog = "master", UserID = "sa",
                Password = Environment.GetEnvironmentVariable("MILLWORKS_SQL_TEST_PASSWORD") ?? "Dev@12345678",
                TrustServerCertificate = true, ConnectTimeout = 5
            };
            _master = connection.ConnectionString;
            await SqlAsync($"CREATE DATABASE [{_database}];");
            await SqlAsync($"ALTER DATABASE [{_database}] SET READ_COMMITTED_SNAPSHOT {(rcsi ? "ON" : "OFF")};");
            connection.InitialCatalog = _database;
            builder.UseSqlServer(connection.ConnectionString, x => x.EnableRetryOnFailure(3));
        }
        _options = builder.AddInterceptors(_fault, _commands).Options;
        _services = new ServiceCollection().AddSingleton(TimeProvider.System)
            .AddSingleton(_options).AddScoped(sp => new AuditDbContext(sp.GetRequiredService<DbContextOptions<AuditDbContext>>()))
            .AddSingleton<OutboxStateStore>().AddSingleton<IOutboxStateStore>(sp => sp.GetRequiredService<OutboxStateStore>()).AddScoped<IAuditOutboxRecovery, AuditOutboxRecovery>()
            .AddSingleton<IAuditOutboxRecoveryAuthorization>(_authorization)
            .AddSingleton(_processor.Object).BuildServiceProvider();
        _store = _services.GetRequiredService<OutboxStateStore>();
        await using var db = Db();
        await db.Database.EnsureCreatedAsync();
    }

    [SetUp]
    public async Task ResetTest()
    {
        _commands.Gate = null; _commands.FailReceiptVerification = false;
        _fault.Reset(); _authorization.Deny = false; _authorization.ExpireAfterAcquire = false;
        _processor.Reset(); _dlq.Reset(); _tenant = Guid.NewGuid();
        await using var reset = Db();
        await reset.AuditOutboxFailureHandoffs.ExecuteDeleteAsync();
        await reset.AuditOutboxResolutions.ExecuteDeleteAsync();
        await reset.AuditOutboxOperations.ExecuteDeleteAsync();
        await reset.AuditOutbox.ExecuteDeleteAsync();
    }

    [OneTimeTearDown]
    public async Task Teardown()
    {
        _fault.Reset();
        if (_services is not null) await _services.DisposeAsync();
        if (_keeper is not null) { await _keeper.DisposeAsync(); _keeper = null; }
        if (_database is not null && _master is not null)
        {
            await SqlAsync($"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
            TestContext.Out.WriteLine($"Dropped owned fixture {_database}; RCSI={rcsi}");
            _database = null;
        }
    }

    private AuditDbContext Db() => new(_options);
    private async Task SqlAsync(string sql)
    {
        await using var connection = new SqlConnection(_master);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
    private string Payload() => JsonSerializer.Serialize(new AuditEnvelope
    {
        Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "Test", EventType = "Test.Event",
        Action = AuditAction.Unknown, ExplicitEventId = Guid.NewGuid(),
        GovernanceIdentity = new(_tenant)
    }, QueuedEnvelopeValidator.Json);
    private async Task<Guid[]> Seed(int count = 1, string? json = null, int version = TransactionalOutboxSink.CurrentEnvelopeVersion)
    {
        await using var db = Db();
        var rows = Enumerable.Range(0, count).Select(i => new AuditOutboxEntity
        {
            IdempotencyKey = Guid.NewGuid(), EnvelopeJson = json ?? Payload(), EnvelopeVersion = version,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10).AddSeconds(i)
        }).ToArray();
        db.AuditOutbox.AddRange(rows); await db.SaveChangesAsync(); return rows.Select(x => x.Id).ToArray();
    }
    private Task<OutboxOperationResult> Claim(int count = 128) => _store.ClaimAsync(Guid.NewGuid(), count, TimeSpan.FromMinutes(1), default);
    private async Task Expire(Guid id)
    {
        await using var db = Db();
        await db.AuditOutbox.Where(x => x.Id == id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
    private async Task<OutboxClaim> Fail()
    {
        await Seed(); var claim = (await Claim()).Claims.Single();
        await _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Failed)], default);
        return claim;
    }
    private async Task<AuditOutboxEntity> Row(Guid id)
    {
        await using var db = Db(); return await db.AuditOutbox.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [TestCase(AuditOutboxStatus.Completed)]
    [TestCase(AuditOutboxStatus.Pending)]
    [TestCase(AuditOutboxStatus.Failed)]
    public async Task ReclaimedRowRejectsEveryStaleOutcome(AuditOutboxStatus outcome)
    {
        await Seed(); var a = (await Claim()).Claims.Single(); await Expire(a.Id);
        await _store.RecoverAsync(Guid.NewGuid(), 128, default);
        var b = (await Claim()).Claims.Single();
        var result = await _store.ApplyAsync(Guid.NewGuid(), [new(a.Id, a.Token, outcome)], default);
        var row = await Row(a.Id);
        Assert.Multiple(() =>
        {
            Assert.That(result.Applied, Is.Empty); Assert.That(result.Rejected, Is.EqualTo(new[] { a.Id }));
            Assert.That(b.Token, Is.Not.EqualTo(a.Token)); Assert.That(row.LeaseOwner, Is.EqualTo(b.Token));
            Assert.That(row.Status, Is.EqualTo(AuditOutboxStatus.InFlight)); Assert.That(row.AttemptCount, Is.Zero);
        });
        await using var db = Db(); Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task SecondAcquisitionNeverReturnsFirstClaims()
    {
        await Seed(2); var first = await Claim(1); var second = await Claim(1);
        Assert.Multiple(() =>
        {
            Assert.That(second.Claims.Single().Id, Is.Not.EqualTo(first.Claims.Single().Id));
            Assert.That(second.Claims.Single().Token, Is.Not.EqualTo(first.Claims.Single().Token));
        });
    }

    [Test]
    public async Task ExpiredCurrentTokenCanFinishUntilRecoveryWins()
    {
        await Seed(); var claim = (await Claim()).Claims.Single(); await Expire(claim.Id);
        var result = await _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default);
        var recovered = await _store.RecoverAsync(Guid.NewGuid(), 128, default);
        Assert.That(result.Applied, Is.EqualTo(new[] { claim.Id })); Assert.That(recovered.Applied, Is.Empty);
    }

    [Test]
    public async Task GroupedOutcomesIdentifyPartialOwnershipAndBoundCommands()
    {
        await Seed(32); var claims = (await Claim()).Claims;
        await using (var db = Db())
            await db.AuditOutbox.Where(x => x.Id == claims[0].Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseOwner, "another-claim"));
        _commands.Count = 0;
        var result = await _store.ApplyAsync(Guid.NewGuid(), claims.Select(x => new OutboxChange(x.Id, x.Token, AuditOutboxStatus.Completed)).ToArray(), default);
        Assert.Multiple(() =>
        {
            Assert.That(result.Applied.Length, Is.EqualTo(31)); Assert.That(result.Rejected, Is.EqualTo(new[] { claims[0].Id }));
            Assert.That(_commands.Count, Is.LessThanOrEqualTo(10), "normal outcomes must not update per row");
        });
    }

    [Test]
    public async Task StableOperationDoesNotIncrementAttemptsOrCreateDuplicateHandoffs()
    {
        await Seed(); var claim = (await Claim()).Claims.Single(); var operation = Guid.NewGuid();
        var changes = new[] { new OutboxChange(claim.Id, claim.Token, AuditOutboxStatus.Failed) };
        var first = await _store.ApplyAsync(operation, changes, default);
        var second = await _store.ApplyAsync(operation, changes, default);
        await using var db = Db();
        Assert.Multiple(() =>
        {
            Assert.That(second.Applied, Is.EqualTo(first.Applied));
            Assert.That(db.AuditOutbox.Single().AttemptCount, Is.EqualTo(1));
            Assert.That(db.AuditOutboxFailureHandoffs.Count(), Is.EqualTo(1));
            Assert.That(db.AuditOutboxOperations.Any(x => x.ResultJson.Contains("EnvelopeJson")), Is.False);
        });
        await Assert.ThatAsync(() => _store.ApplyAsync(operation,
            [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default), Throws.TypeOf<AuditOutboxOperationConflictException>());
    }

    [Test]
    public async Task WriterContextCannotFlushClaimedOutboxState()
    {
        await Seed();
        await using var scope = _services.CreateAsyncScope(); var writer = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var claim = (await Claim()).Claims.Single();
        Assert.That(writer.ChangeTracker.Entries<AuditOutboxEntity>(), Is.Empty);
        await writer.SaveChangesAsync();
        Assert.That((await Row(claim.Id)).LeaseOwner, Is.EqualTo(claim.Token));
    }

    [Test]
    public async Task RecoveryIsBoundedAndDoesNotConsumeAttempts()
    {
        await Seed(3); var claims = (await Claim()).Claims;
        foreach (var claim in claims) await Expire(claim.Id);
        var recovered = await _store.RecoverAsync(Guid.NewGuid(), 2, default);
        Assert.That(recovered.Applied.Length, Is.EqualTo(2));
        await using var db = Db(); Assert.That(await db.AuditOutbox.CountAsync(x => x.AttemptCount != 0), Is.Zero);
        Assert.That(await db.AuditOutbox.CountAsync(x => x.Status == AuditOutboxStatus.InFlight), Is.EqualTo(1));
    }

    [TestCase("before", "claim")]
    [TestCase("after", "claim")]
    [TestCase("before", "outcome")]
    [TestCase("after", "outcome")]
    [TestCase("before", "failure")]
    [TestCase("after", "failure")]
    [TestCase("before", "recover")]
    [TestCase("after", "recover")]
    [TestCase("before", "handoff_claim")]
    [TestCase("after", "handoff_claim")]
    [TestCase("before", "handoff_recover")]
    [TestCase("after", "handoff_recover")]
    [TestCase("before", "handoff_ack")]
    [TestCase("after", "handoff_ack")]
    public async Task SqlCommitFaultsResolveThroughImmutableEvidence(string position, string operationKind)
    {
        if (provider != "sql") Assert.Ignore("SQL execution-strategy fault coverage.");
        await Seed();
        OutboxClaim? claim = null; HandoffClaim? handoff = null;
        if (operationKind != "claim") claim = (await Claim()).Claims.Single();
        if (operationKind == "recover") await Expire(claim!.Id);
        if (operationKind.StartsWith("handoff_"))
        {
            await _store.ApplyAsync(Guid.NewGuid(), [new(claim!.Id, claim.Token, AuditOutboxStatus.Failed)], default);
            if (operationKind != "handoff_claim")
                handoff = (await _store.ClaimHandoffsAsync(Guid.NewGuid(), 128, TimeSpan.FromMinutes(1), default)).Handoffs.Single();
            if (operationKind == "handoff_recover")
            {
                await using var expire = Db();
                await expire.AuditOutboxFailureHandoffs.ExecuteUpdateAsync(x => x.SetProperty(h => h.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
        }
        _fault.Arm(position);
        var operation = Guid.NewGuid();
        var result = operationKind switch
        {
            "claim" => await _store.ClaimAsync(operation, 128, TimeSpan.FromMinutes(1), default),
            "recover" => await _store.RecoverAsync(operation, 128, default),
            "handoff_claim" or "handoff_recover" => await _store.ClaimHandoffsAsync(operation, 128, TimeSpan.FromMinutes(1), default),
            "handoff_ack" => await _store.AcknowledgeHandoffAsync(operation, handoff!, true, default),
            _ => await _store.ApplyAsync(operation, [new(claim!.Id, claim.Token,
                operationKind == "failure" ? AuditOutboxStatus.Failed : AuditOutboxStatus.Completed)], default)
        };
        Assert.That(result.Applied.Length, Is.EqualTo(1));
        Assert.That(_fault.Faults, Is.EqualTo(1));
        Assert.That((await _store.VerifyAsync(operation, default))!.Applied, Is.EqualTo(result.Applied));
        await using var db = Db();
        Assert.That(await db.AuditOutboxOperations.CountAsync(x => x.Id == operation), Is.EqualTo(1));
        if (operationKind is "failure" or "handoff_ack") Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.EqualTo(1));
        if (operationKind == "failure") Assert.That((await Row(claim!.Id)).AttemptCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReceiptSurvivesLaterStateChangesAndClearedToken()
    {
        await Seed(); var operation = Guid.NewGuid();
        var first = await _store.ClaimAsync(operation, 128, TimeSpan.FromMinutes(1), default);
        var claim = first.Claims.Single();
        await _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default);
        var verified = await _store.ClaimAsync(operation, 128, TimeSpan.FromMinutes(1), default);
        Assert.That(verified.Claims.Single(), Is.EqualTo(claim));
        Assert.That((await Row(claim.Id)).Status, Is.EqualTo(AuditOutboxStatus.Completed));
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task FailureAndProjectionAreDurableWithoutRepublishingAudit(bool deliverySucceeds)
    {
        var source = await Fail();
        long routed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, owner) => { if (instrument.Name == AuditMetrics.Names.DlqRouted) owner.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => routed += value); listener.Start();
        if (deliverySucceeds) _dlq.Setup(x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception?>(), It.IsAny<string?>())).Returns(Task.CompletedTask);
        else _dlq.Setup(x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception?>(), It.IsAny<string?>())).ThrowsAsync(new IOException("offline"));
        var dispatcher = new OutboxHandoffDispatcher(_store, NullLogger<OutboxHandoffDispatcher>.Instance, _dlq.Object);
        var count = await dispatcher.DispatchAsync(128, TimeSpan.FromMinutes(1), default);
        await using var db = Db(); var handoff = await db.AuditOutboxFailureHandoffs.SingleAsync();
        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(deliverySucceeds ? 1 : 0)); Assert.That(routed, Is.EqualTo(deliverySucceeds ? 1 : 0));
            Assert.That(handoff.Status, Is.EqualTo(deliverySucceeds ? AuditOutboxHandoffStatus.Delivered : AuditOutboxHandoffStatus.Pending));
            Assert.That(handoff.EnvelopeJson, Is.Not.Empty);
        });
        var row = await Row(source.Id); Assert.That(row.Status, Is.EqualTo(AuditOutboxStatus.Failed)); Assert.That(row.ResolutionId, Is.Null);
        _processor.Verify(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task MissingDlqLeavesPendingProjectionWithoutClaims()
    {
        await Fail(); var dispatcher = new OutboxHandoffDispatcher(_store, NullLogger<OutboxHandoffDispatcher>.Instance);
        Assert.That(await dispatcher.DispatchAsync(128, TimeSpan.FromMinutes(1), default), Is.Zero);
        await using var db = Db(); Assert.That((await db.AuditOutboxFailureHandoffs.SingleAsync()).Status, Is.EqualTo(AuditOutboxHandoffStatus.Pending));
    }

    [Test]
    public async Task CrashAfterEnqueueRecoversSameHandoffAndRejectsStaleAck()
    {
        await Fail(); var a = (await _store.ClaimHandoffsAsync(Guid.NewGuid(), 128, TimeSpan.FromMinutes(1), default)).Handoffs.Single();
        await using (var db = Db()) await db.AuditOutboxFailureHandoffs.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        var b = (await _store.ClaimHandoffsAsync(Guid.NewGuid(), 128, TimeSpan.FromMinutes(1), default)).Handoffs.Single();
        var oldAck = await _store.AcknowledgeHandoffAsync(Guid.NewGuid(), a, true, default);
        var newAck = await _store.AcknowledgeHandoffAsync(Guid.NewGuid(), b, true, default);
        Assert.Multiple(() =>
        {
            Assert.That(b.Id, Is.EqualTo(a.Id)); Assert.That(b.Token, Is.Not.EqualTo(a.Token));
            Assert.That(oldAck.Applied, Is.Empty); Assert.That(newAck.Applied, Is.EqualTo(new[] { b.Id }));
        });
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AuthorizedResolutionSuppressesProjectionAndRetainsEvidence(bool replay)
    {
        var source = await Fail(); var row = await Row(source.Id);
        var inflight = (await _store.ClaimHandoffsAsync(Guid.NewGuid(), 128, TimeSpan.FromMinutes(1), default)).Handoffs.Single();
        await using var scope = _services.CreateAsyncScope(); var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        var resolution = Guid.NewGuid();
        var result = replay ? await recovery.ReplayAsync(resolution, row.Id, _tenant, row.FailureCycleId!.Value, "approved retry")
            : await recovery.SkipAsync(resolution, row.Id, _tenant, row.FailureCycleId!.Value, "approved skip");
        Assert.Multiple(() => { Assert.That(result.Applied, Is.True); Assert.That(result.ExternalDeliveryMayBeInFlight, Is.True); });
        Assert.That((await _store.AcknowledgeHandoffAsync(Guid.NewGuid(), inflight, true, default)).Applied, Is.Empty);
        var updated = await Row(row.Id);
        Assert.That(updated.Status, Is.EqualTo(replay ? AuditOutboxStatus.Pending : AuditOutboxStatus.Failed));
        Assert.That(updated.ResolutionId, Is.EqualTo(resolution));
        await using var db = Db(); var evidence = await db.AuditOutboxResolutions.SingleAsync();
        Assert.Multiple(() => { Assert.That(evidence.ActorId, Is.EqualTo("trusted-operator")); Assert.That(evidence.Reason, Is.Not.Empty); });
        if (replay)
        {
            var next = (await Claim()).Claims.Single();
            Assert.That(next.AttemptCount, Is.Zero); Assert.That(next.Token, Is.Not.EqualTo(source.Token));
            await _store.ApplyAsync(Guid.NewGuid(), [new(next.Id, next.Token, AuditOutboxStatus.Failed)], default);
            var latest = await Row(next.Id); Assert.That(latest.FailureCycleId, Is.Not.EqualTo(row.FailureCycleId));
            Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.EqualTo(2));
        }
    }

    [Test]
    public async Task RecoveryRejectsUnauthorizedCrossTenantExpiredAndStaleCycleRequests()
    {
        var source = await Fail(); var row = await Row(source.Id);
        await using var scope = _services.CreateAsyncScope(); var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        _authorization.Deny = true;
        await Assert.ThatAsync(() => recovery.InspectAsync(row.Id, _tenant), Throws.TypeOf<UnauthorizedAccessException>());
        _authorization.Deny = false;
        Assert.That(await recovery.InspectAsync(row.Id, Guid.NewGuid()), Is.Null);
        Assert.That((await recovery.SkipAsync(Guid.NewGuid(), row.Id, Guid.NewGuid(), row.FailureCycleId!.Value, "wrong tenant")).Applied, Is.False);
        Assert.That((await recovery.ReplayAsync(Guid.NewGuid(), row.Id, _tenant, Guid.NewGuid(), "stale cycle")).Applied, Is.False);
        _authorization.ExpireAfterAcquire = true;
        await Assert.ThatAsync(() => recovery.SkipAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId!.Value, "expired permit"), Throws.TypeOf<UnauthorizedAccessException>());
        Assert.That((await Row(row.Id)).ResolutionId, Is.Null);
    }

    [Test]
    public async Task CleanupRequiresResolutionAndExpiredVerificationWindow()
    {
        var source = await Fail(); var row = await Row(source.Id);
        await using var scope = _services.CreateAsyncScope(); var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
        Assert.That(await recovery.CleanupAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId!.Value), Is.False);
        await recovery.SkipAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId.Value, "approved disposal");
        Assert.That(await recovery.CleanupAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId.Value), Is.False);
        await using (var db = Db())
        {
            var old = DateTimeOffset.UtcNow.AddDays(-2);
            await db.AuditOutboxOperations.ExecuteUpdateAsync(s => s.SetProperty(x => x.VerifyUntil, old));
            await db.AuditOutboxResolutions.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, old));
            await db.AuditOutboxFailureHandoffs.ExecuteUpdateAsync(s => s.SetProperty(x => x.CompletedAt, old));
        }
        Assert.That(await recovery.CleanupAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId.Value), Is.True);
        await using var verify = Db();
        Assert.Multiple(() =>
        {
            Assert.That(verify.AuditOutbox.Count(), Is.Zero); Assert.That(verify.AuditOutboxFailureHandoffs.Count(), Is.Zero);
            Assert.That(verify.AuditOutboxResolutions.Count(), Is.EqualTo(2)); Assert.That(verify.AuditOutboxOperations.Count(), Is.GreaterThan(0));
            Assert.That(verify.AuditOutboxResolutions.Single(x => x.Action == "Cleanup").ActorId, Is.EqualTo("trusted-operator"));
        });
    }

    [TestCase("{}", 1)]
    [TestCase("null", 1)]
    [TestCase("not-json", 1)]
    [TestCase("{\"envelopeId\":\"00000000-0000-0000-0000-000000000000\"}", 1)]
    [TestCase("{}", 99)]
    public async Task InvalidQueuedContractsBecomeRetainedFailuresBeforeWriters(string json, int version)
    {
        var id = (await Seed(json: json, version: version)).Single();
        using var observer = new AuditOutboxQueueObserver();
        var drainer = new AuditOutboxDrainer(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(new SecurityOptions()), observer);
        await using var scope = _services.CreateAsyncScope();
        Assert.That(await drainer.DrainBatchAsync(scope.ServiceProvider, default), Is.EqualTo(1));
        Assert.That((await Row(id)).Status, Is.EqualTo(AuditOutboxStatus.Failed));
        _processor.Verify(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task CancellationDoesNotCreateTerminalFailureOrConsumeAttempts()
    {
        await Seed(); var claim = (await Claim()).Claims.Single();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThatAsync(() => _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Failed)], canceled.Token), Throws.InstanceOf<OperationCanceledException>());
        Assert.That((await Row(claim.Id)).Status, Is.EqualTo(AuditOutboxStatus.InFlight));
        await using var db = Db(); Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.Zero);
    }

    private sealed class TestAuthorization : IAuditOutboxRecoveryAuthorization
    {
        public bool Deny; public bool ExpireAfterAcquire;
        public ValueTask<IAuditOutboxRecoveryPermit> AcquireAsync(AuditOutboxRecoveryTarget target, AuditOutboxRecoveryAction action, CancellationToken cancellationToken)
        {
            if (Deny) throw new UnauthorizedAccessException();
            return ValueTask.FromResult<IAuditOutboxRecoveryPermit>(new Permit(target.TenantId, this));
        }
        private sealed class Permit(Guid? tenant, TestAuthorization owner) : IAuditOutboxRecoveryPermit
        {
            private int _checks;
            public string ActorId => "trusted-operator";
            public Guid? TenantId => tenant;
            public ValueTask CheckCurrentAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.ExpireAfterAcquire && ++_checks > 1) throw new UnauthorizedAccessException();
                return ValueTask.CompletedTask;
            }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class CommitFault : DbTransactionInterceptor
    {
        private string? _position;
        public int Faults;
        public void Reset() { _position = null; Faults = 0; }
        public void Arm(string position) { _position = position; Faults = 0; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (_position == "before") { _position = null; Faults++; throw new TimeoutException("Injected before SQL commit"); }
            return ValueTask.FromResult(result);
        }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_position == "after") { _position = null; Faults++; throw new TimeoutException("Injected lost SQL commit acknowledgement"); }
            return Task.CompletedTask;
        }
    }
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count;
        public bool FailReceiptVerification;
        public LockedReadGate? Gate;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            var gate = Gate;
            if (gate is not null && command.CommandText.Contains("AuditOutbox] WITH (UPDLOCK, ROWLOCK)") && gate.TryEnter(command))
                await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            if (FailReceiptVerification && command.CommandText.Contains("AuditOutboxOperations") &&
                command.CommandTimeout == 5) throw new IOException("Injected unavailable commit verifier");
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) { Count++; return ValueTask.FromResult(result); }
    }
}
