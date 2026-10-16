using System.Data.Common;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Tests.Sinks;

public sealed partial class OutboxOwnershipTests
{
    private sealed class LockedReadGate
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SessionId;
        private int _entered;
        public bool TryEnter(DbCommand command)
        {
            if (Interlocked.Exchange(ref _entered, 1) != 0) return false;
            SessionId = ((SqlConnection)command.Connection!).ServerProcessId;
            Reached.SetResult(); return true;
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task OverlappingCompletionAndRecoveryHaveExactlyOneWinner(bool recoveryFirst)
    {
        if (provider != "sql") Assert.Ignore("Real SQL lock overlap.");
        await Seed(); var claim = (await Claim()).Claims.Single(); await Expire(claim.Id);
        var gate = new LockedReadGate(); _commands.Gate = gate;
        var first = recoveryFirst ? _store.RecoverAsync(Guid.NewGuid(), 128, default)
            : _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default);
        Task<OutboxOperationResult>? second = null;
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = recoveryFirst ? _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default)
                : _store.RecoverAsync(Guid.NewGuid(), 128, default);
            // The observer is a third connection. Prove SQL is actually waiting for the
            // held transaction; no timing sleep or duplicated mutation logic is involved.
            await using var observer = new SqlConnection(_master);
            await observer.OpenAsync();
            var watch = Stopwatch.StartNew();
            var blocked = false;
            while (!blocked && watch.Elapsed < TimeSpan.FromSeconds(8))
            {
                await using var command = observer.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sys.dm_exec_requests WHERE blocking_session_id = @owner AND wait_type LIKE 'LCK%';";
                command.Parameters.AddWithValue("@owner", gate.SessionId);
                blocked = Convert.ToInt32(await command.ExecuteScalarAsync()) != 0;
            }
            Assert.That(blocked, Is.True, "A real concurrent SQL operation must wait behind the first transition");
        }
        finally { _commands.Gate = null; gate.Release.TrySetResult(); }
        var a = await first; var b = await second!;
        Assert.That(a.Applied.Length + b.Applied.Length, Is.EqualTo(1));
        var recoveryWon = recoveryFirst ? a.Applied.Length == 1 : b.Applied.Length == 1;
        var row = await Row(claim.Id);
        Assert.That(row.Status, Is.EqualTo(recoveryWon ? AuditOutboxStatus.Pending : AuditOutboxStatus.Completed));
    }

    [Test]
    public async Task OperatorSuppressingInFlightExternalWriteRejectsAckWithoutPretendingToRetractDelivery()
    {
        var source = await Fail(); var row = await Row(source.Id);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _dlq.Setup(x => x.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception?>(), It.IsAny<string?>()))
            .Returns(async () => { entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10)); });
        var dispatcher = new OutboxHandoffDispatcher(_store, NullLogger<OutboxHandoffDispatcher>.Instance, _dlq.Object);
        var delivery = dispatcher.DispatchAsync(128, TimeSpan.FromMinutes(1), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var scope = _services.CreateAsyncScope();
            var recovery = scope.ServiceProvider.GetRequiredService<IAuditOutboxRecovery>();
            var resolved = await recovery.SkipAsync(Guid.NewGuid(), row.Id, _tenant, row.FailureCycleId!.Value, "authorized skip");
            Assert.Multiple(() => { Assert.That(resolved.Applied, Is.True); Assert.That(resolved.ExternalDeliveryMayBeInFlight, Is.True); });
        }
        finally { release.TrySetResult(); }
        Assert.That(await delivery, Is.Zero, "suppressed acknowledgement is not a completed handoff");
        await using var db = Db(); Assert.That((await db.AuditOutboxFailureHandoffs.SingleAsync()).Status, Is.EqualTo(AuditOutboxHandoffStatus.Suppressed));
    }

    [Test]
    public async Task AgeOnlySourceDeletionIsBlockedWhileFailureProjectionExists()
    {
        var source = await Fail();
        await using var db = Db();
        await Assert.ThatAsync(() => db.AuditOutbox.Where(x => x.Id == source.Id).ExecuteDeleteAsync(), Throws.InstanceOf<DbException>());
        Assert.That(await db.AuditOutbox.CountAsync(x => x.Id == source.Id), Is.EqualTo(1));
        Assert.That(await db.AuditOutboxFailureHandoffs.CountAsync(), Is.EqualTo(1));
    }

    [Test]
    public async Task NoOpPollingDoesNotAccumulateReceipts()
    {
        await Claim(); await _store.RecoverAsync(Guid.NewGuid(), 128, default);
        await _store.ClaimHandoffsAsync(Guid.NewGuid(), 128, TimeSpan.FromMinutes(1), default);
        await using var db = Db(); Assert.That(await db.AuditOutboxOperations.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task PortableClaimReadsNativeConsumerTimestampFormat()
    {
        if (provider != "sqlite") Assert.Ignore("Native SQLite consumer mapping.");
        var id = Guid.NewGuid();
        await using (var db = Db())
            await db.Database.ExecuteSqlRawAsync("INSERT INTO AuditOutbox (Id, IdempotencyKey, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) VALUES ({0},{1},{2},1,0,{3},0)",
                id, id, Payload(), DateTimeOffset.UtcNow.AddHours(-1));
        Assert.That((await Claim()).Claims.Single().Id, Is.EqualTo(id));
    }

    [TestCase("kind")]
    [TestCase("envelopeId")]
    [TestCase("occurredAt")]
    [TestCase("eventType")]
    [TestCase("entityName")]
    public async Task StructurallyValidJsonStillRequiresTheQueuedContract(string field)
    {
        var payload = JsonNode.Parse(Payload())!.AsObject();
        if (field == "kind") payload[field] = 999;
        else payload.Remove(field);
        var id = (await Seed(json: payload.ToJsonString())).Single();
        using var observer = new AuditOutboxQueueObserver();
        var drainer = new AuditOutboxDrainer(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(new SecurityOptions()), observer);
        await using var scope = _services.CreateAsyncScope();
        await drainer.DrainBatchAsync(scope.ServiceProvider, default);
        Assert.That((await Row(id)).Status, Is.EqualTo(AuditOutboxStatus.Failed));
        _processor.Verify(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}

public sealed partial class OutboxOwnershipTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task BatchOutlivingItsLeaseFinalizesOnlyWhileItsOriginalTokenIsCurrent(bool recoveryWins)
    {
        await Seed();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _processor.Setup(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<ClaimedOutboxRow> rows, CancellationToken ct) =>
            {
                entered.SetResult(); await release.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
                return new BatchProcessingResult { Outcomes = rows.Select(x => RowOutcome.Success(x.RowId)).ToArray() };
            });
        using var observer = new AuditOutboxQueueObserver();
        var drainer = new AuditOutboxDrainer(_services.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(new SecurityOptions()), observer);
        await using var scope = _services.CreateAsyncScope();
        var processing = drainer.DrainBatchAsync(scope.ServiceProvider, default);
        OutboxClaim? next = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var writerContext = scope.ServiceProvider.GetRequiredService<MillWorks.AuditCore.EntityFramework.Data.AuditDbContext>();
            Assert.That(writerContext.ChangeTracker.Entries<AuditOutboxEntity>(), Is.Empty);
            await using var db = Db(); var row = await db.AuditOutbox.AsNoTracking().SingleAsync();
            await Expire(row.Id);
            if (recoveryWins)
            {
                await _store.RecoverAsync(Guid.NewGuid(), 128, default);
                next = (await Claim()).Claims.Single();
            }
        }
        finally { release.TrySetResult(); }
        Assert.That(await processing, Is.EqualTo(recoveryWins ? 0 : 1));
        await using var verify = Db(); var final = await verify.AuditOutbox.AsNoTracking().SingleAsync();
        Assert.That(final.Status, Is.EqualTo(recoveryWins ? AuditOutboxStatus.InFlight : AuditOutboxStatus.Completed));
        if (next is not null) Assert.That(final.LeaseOwner, Is.EqualTo(next.Token));
    }

    [Test]
    public async Task DefaultRecoveryPolicyDeniesOperatorActions()
    {
        var recovery = new AuditOutboxRecovery(_store, _services.GetRequiredService<IServiceScopeFactory>());
        await Assert.ThatAsync(() => recovery.InspectAsync(Guid.NewGuid(), _tenant), Throws.TypeOf<UnauthorizedAccessException>());
    }

    [Test]
    public async Task ClockSkewCannotAuthorizeStaleTokenFinalization()
    {
        await Seed(); var claim = (await Claim()).Claims.Single();
        var future = new OutboxStateStore(_services.GetRequiredService<IServiceScopeFactory>(), new FutureClock());
        var recovered = await future.RecoverAsync(Guid.NewGuid(), 128, default);
        Assert.That(recovered.Applied.Length, Is.EqualTo(provider == "sql" ? 0 : 1));
        if (provider == "sql") { await Expire(claim.Id); await _store.RecoverAsync(Guid.NewGuid(), 128, default); }
        var next = (await Claim()).Claims.Single();
        Assert.That((await _store.ApplyAsync(Guid.NewGuid(), [new(claim.Id, claim.Token, AuditOutboxStatus.Completed)], default)).Applied, Is.Empty);
        Assert.That((await Row(claim.Id)).LeaseOwner, Is.EqualTo(next.Token));
    }

    private sealed class FutureClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddDays(2);
    }
}
