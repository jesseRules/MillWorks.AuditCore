using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Sinks.Writers;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Tests.Integration;

public sealed partial class EnvelopeIdentitySqliteTests
{
    [Test]
    public async Task MixedPersistenceAliasesConflictsAndFailuresHaveExactOutcomesAndMetrics()
    {
        var old = Event();
        await Publish(false, old);
        var replay = old with { EnvelopeId = Guid.NewGuid() };
        var fresh = Event();
        var alias = fresh with { EnvelopeId = Guid.NewGuid() };
        var conflict = old with { EnvelopeId = Guid.NewGuid(), UserId = "changed" };
        var failure = Event() with { Kind = AuditEnvelopeKind.EntityChange, ExplicitEventId = null };
        AuditEnvelope[] inputs = [fresh, replay, alias, conflict, failure];
        var counts = new Dictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == AuditMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var error = "";
            foreach (var tag in tags) if (tag.Key == AuditMetrics.Tags.ErrorType) error = ":" + tag.Value;
            var key = instrument.Name + error;
            counts[key] = counts.GetValueOrDefault(key) + value;
        });
        listener.Start();
        await using var db = CreateContext();
        var entity = new Mock<IAuditEntityBatchWriter>();
        entity.Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { WriteOutcome.Failed(failure.EnvelopeId, "test failure", false) });
        var processor = new AuditBatchProcessor(entity.Object, Writer(db), TimeProvider.System, NullLogger<AuditBatchProcessor>.Instance);
        var rows = inputs.Select(e => new ClaimedOutboxRow
            { RowId = Guid.NewGuid(), Envelope = e, AttemptCount = 0, CreatedAt = DateTimeOffset.UtcNow }).ToArray();
        var result = await processor.ProcessBatchAsync(rows, default);
        foreach (var row in rows)
        {
            var expected = row.Envelope == fresh ? RowStatus.Succeeded
                : row.Envelope == replay || row.Envelope == alias ? RowStatus.Duplicate : RowStatus.Failed;
            Assert.That(result.Outcomes.Single(x => x.RowId == row.RowId).Status, Is.EqualTo(expected));
        }
        Assert.That(counts[AuditMetrics.Names.EnvelopesPublished], Is.EqualTo(1));
        Assert.That(counts[AuditMetrics.Names.EnvelopesDuplicate], Is.EqualTo(2));
        Assert.That(counts[AuditMetrics.Names.EnvelopesFailed + ":identity_conflict"], Is.EqualTo(1));
        Assert.That(counts[AuditMetrics.Names.EnvelopesFailed + ":unknown"], Is.EqualTo(1));
        Assert.That(await db.AuditEvents.CountAsync(), Is.EqualTo(2));
        // Single-item and all-replay paths must retain their duplicate classification too.
        Assert.That((await Writer(db).WriteBatchAsync([replay], default)).Single().IsDuplicate, Is.True);
        Assert.That((await Writer(db).WriteBatchAsync([replay, alias], default)).All(x => x.IsDuplicate), Is.True);
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("unknown")]
    [TestCase("contradictory")]
    public async Task MalformedWriterResultsLeaveCommittedEventsRecoverable(string defect)
    {
        var first = Event(); var second = Event();
        await Publish(true, first); await Publish(true, second);
        await using var db = CreateContext();
        var writer = new Mock<IAuditEventBatchWriter>();
        writer.Setup(x => x.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .Returns(async (IReadOnlyList<AuditEnvelope> envelopes, CancellationToken ct) =>
            {
                await Writer(db).WriteBatchAsync(envelopes, ct); // durable writes precede the defective response
                return (IReadOnlyList<WriteOutcome>)(defect switch
                {
                    "missing" => [WriteOutcome.Success(first.EnvelopeId)],
                    "duplicate" => [WriteOutcome.Success(first.EnvelopeId), WriteOutcome.Success(first.EnvelopeId)],
                    "unknown" => [WriteOutcome.Success(first.EnvelopeId), WriteOutcome.Success(Guid.NewGuid())],
                    _ => [WriteOutcome.Success(first.EnvelopeId), new WriteOutcome
                        { EnvelopeId = second.EnvelopeId, Succeeded = true, IsDuplicate = true, IsConflict = true }]
                });
            });
        await using var provider = DrainerServices(new AuditBatchProcessor(Mock.Of<IAuditEntityBatchWriter>(), writer.Object,
            TimeProvider.System, NullLogger<AuditBatchProcessor>.Instance)).BuildServiceProvider();
        using var observer = new AuditOutboxQueueObserver();
        using var drainer = new AuditOutboxDrainer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions()), observer);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<AuditBatchContractException>(() => drainer.DrainBatchAsync(scope.ServiceProvider, default));
        await using var check = CreateContext();
        var queued = await check.AuditOutbox.ToListAsync();
        Assert.That(queued.All(x => x.Status == AuditOutboxStatus.InFlight && x.AttemptCount == 0), Is.True);
        Assert.That(await check.AuditOutboxFailureHandoffs.CountAsync(), Is.Zero);
        Assert.That(await check.AuditEvents.CountAsync(), Is.EqualTo(2));
        // Replay after lease recovery is safe and content verified.
        Assert.That((await Writer(check).WriteBatchAsync([first, second], default)).All(x => x.IsDuplicate), Is.True);
    }

    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("unknown")]
    [TestCase("contradictory")]
    [TestCase("unrecognized")]
    public async Task MalformedProcessorResultsApplyNoRowTransitions(string defect)
    {
        await Publish(true, Event());
        var processor = new Mock<IAuditBatchProcessor>();
        processor.Setup(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ClaimedOutboxRow> rows, CancellationToken _) => new BatchProcessingResult
            {
                Outcomes = defect switch
                {
                    "missing" => [],
                    "duplicate" => [RowOutcome.Success(rows[0].RowId), RowOutcome.Failed(rows[0].RowId, "contradiction")],
                    "unknown" => [RowOutcome.Success(Guid.NewGuid())],
                    "unrecognized" => [new RowOutcome { RowId = rows[0].RowId, Status = (RowStatus)999 }],
                    _ => [new RowOutcome { RowId = rows[0].RowId, Status = RowStatus.Succeeded, IsRetryable = true }]
                }
            });
        await using var provider = DrainerServices(processor.Object).BuildServiceProvider();
        using var observer = new AuditOutboxQueueObserver();
        using var drainer = new AuditOutboxDrainer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions()), observer);
        await using var scope = provider.CreateAsyncScope();
        await Assert.ThrowsAsync<AuditBatchContractException>(() => drainer.DrainBatchAsync(scope.ServiceProvider, default));
        await using var check = CreateContext();
        var queued = await check.AuditOutbox.SingleAsync();
        Assert.That(queued.Status, Is.EqualTo(AuditOutboxStatus.InFlight));
        Assert.That(queued.AttemptCount, Is.Zero);
        Assert.That(await check.AuditOutboxFailureHandoffs.CountAsync(), Is.Zero);
    }

    [Test]
    public async Task PersistentContractDefectsEscalateAfterTheConfiguredThreshold()
    {
        await Publish(true, Event());
        var calls = 0;
        var processor = new Mock<IAuditBatchProcessor>();
        processor.Setup(x => x.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref calls);
                throw new AuditBatchContractException("Injected invalid processor");
            });
        var services = DrainerServices(processor.Object);
        services.AddSingleton<MillWorks.AuditCore.Services.DistributedLocking.Interfaces.IAuditDistributedLockService,
            MillWorks.AuditCore.Services.DistributedLocking.Implementations.NullDistributedLockService>();
        await using var provider = services.BuildServiceProvider();
        var options = new SecurityOptions
        {
            AuditSinkMode = MillWorks.AuditCore.Abstractions.Dto.AuditSinkMode.TransactionalOutbox,
            OutboxDrainerPollInterval = TimeSpan.FromMilliseconds(5), OutboxDrainerLeaseDuration = TimeSpan.FromMilliseconds(100),
            OutboxDrainerLeaseRecoveryInterval = TimeSpan.FromMilliseconds(10), OutboxDrainerCircuitBreakerThreshold = 2
        };
        using var observer = new AuditOutboxQueueObserver();
        using var drainer = new AuditOutboxDrainer(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AuditOutboxDrainer>.Instance,
            Microsoft.Extensions.Options.Options.Create(options), observer);
        await drainer.StartAsync(default);
        try
        {
            await Assert.ThrowsAsync<AuditBatchContractException>(async () =>
                await drainer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.That(calls, Is.EqualTo(2));
            await using var check = CreateContext();
            Assert.That(await check.AuditOutbox.AnyAsync(x => x.Status == AuditOutboxStatus.Failed || x.Status == AuditOutboxStatus.Completed), Is.False);
            Assert.That(await check.AuditOutboxFailureHandoffs.CountAsync(), Is.Zero);
        }
        finally { await drainer.StopAsync(default); }
    }

    private ServiceCollection DrainerServices(IAuditBatchProcessor processor)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(Connection));
        services.AddSingleton<OutboxStateStore>(); services.AddScoped<OutboxHandoffDispatcher>();
        services.AddSingleton(processor);
        return services;
    }
}
