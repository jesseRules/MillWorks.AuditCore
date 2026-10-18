using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Tests.Sinks;

[TestFixture]
public sealed class OutboxDrainerOrchestrationTests
{
    [TestCase(false, false, false)]
    [TestCase(true, false, false)]
    [TestCase(false, true, false)]
    [TestCase(false, false, true)]
    public async Task DrainerKeepsOriginalClaimTokensAndRejectsMalformedResultsWithoutADatabase(bool malformed, bool lostOwnership, bool cancelAfterProcessing)
    {
        using var cancellation = new CancellationTokenSource();
        var id = Guid.NewGuid(); const string token = "original-acquisition";
        var envelope = new AuditEnvelope { Action = AuditAction.Unknown, Kind = AuditEnvelopeKind.ExplicitEvent, ExplicitEventId = Guid.NewGuid(), EventType = "fixture", EntityName = "Test" };
        var state = new Mock<IOutboxStateStore>(MockBehavior.Strict);
        var processor = new Mock<IAuditBatchProcessor>();
        state.Setup(s => s.ClaimAsync(It.IsAny<Guid>(), It.IsAny<int>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new OutboxOperationResult(Guid.NewGuid(), [id], [],
                [new(id, token, JsonSerializer.Serialize(envelope, QueuedEnvelopeValidator.Json), TransactionalOutboxSink.CurrentEnvelopeVersion, 0, DateTimeOffset.UtcNow)], []));
        processor.Setup(p => p.ProcessBatchAsync(It.IsAny<IReadOnlyList<ClaimedOutboxRow>>(), It.IsAny<CancellationToken>()))
            .Callback(() => { if (cancelAfterProcessing) cancellation.Cancel(); })
            .ReturnsAsync(new BatchProcessingResult { Outcomes = malformed ? [] : [new RowOutcome { RowId = id, Status = RowStatus.Succeeded }] });
        if (!malformed && !cancelAfterProcessing)
            state.Setup(s => s.ApplyAsync(It.IsAny<Guid>(), It.Is<IReadOnlyList<OutboxChange>>(c =>
                c.Count == 1 && c[0].Id == id && c[0].Token == token && c[0].Status == AuditOutboxStatus.Completed), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new OutboxOperationResult(Guid.NewGuid(), lostOwnership ? [] : [id], lostOwnership ? [id] : [], [], []));
        await using var services = new ServiceCollection().AddSingleton(state.Object).AddSingleton(processor.Object).BuildServiceProvider();
        using var observer = new AuditOutboxQueueObserver();
        using var drainer = new AuditOutboxDrainer(services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance, Options.Create(new SecurityOptions()), observer);
        if (cancelAfterProcessing)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => drainer.DrainBatchAsync(services, cancellation.Token));
            state.Verify(s => s.ApplyAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<OutboxChange>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else if (malformed)
        {
            await Assert.ThrowsAsync<AuditBatchContractException>(() => drainer.DrainBatchAsync(services, default));
            state.Verify(s => s.ApplyAsync(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<OutboxChange>>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        else Assert.That(await drainer.DrainBatchAsync(services, default), Is.EqualTo(lostOwnership ? 0 : 1));
        state.VerifyAll();
    }
}
