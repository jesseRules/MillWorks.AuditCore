using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
public sealed class InProcessRequestAuditDispatcherTests
{
    [Test]
    public async Task DispatchAsync_RouteToDeadLetter_StoresOverflowedEventWithCorrelationId()
    {
        var capturingDlq = new CapturingDeadLetterQueue();
        var scopeFactory = new Mock<IServiceScopeFactory>();

        var options = Options.Create(new AuditMiddlewareOptions
        {
            QueueCapacity = 2,
            EnqueueTimeout = TimeSpan.Zero,
            OverflowPolicy = RequestAuditOverflowPolicy.RouteToDeadLetter
        });

        using var dispatcher = new InProcessRequestAuditDispatcher(
            scopeFactory.Object,
            options,
            NullLogger<InProcessRequestAuditDispatcher>.Instance,
            capturingDlq);

        const string expectedCorrelationId = "overflow-corr-id";

        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "first" });
        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "second" });

        Assert.That(
            async () => await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = expectedCorrelationId }),
            Throws.Nothing);

        Assert.That(capturingDlq.StoredEvents, Has.Count.EqualTo(1));
        Assert.That(capturingDlq.StoredEvents[0].CorrelationId, Is.EqualTo(expectedCorrelationId));
    }

    [Test]
    public async Task DispatchAsync_Overflow_IncrementsEnqueueTimeoutCounter()
    {
        var diagnostics = new AuditDiagnostics();
        var scopeFactory = new Mock<IServiceScopeFactory>();

        var options = Options.Create(new AuditMiddlewareOptions
        {
            QueueCapacity = 2,
            EnqueueTimeout = TimeSpan.Zero,
            OverflowPolicy = RequestAuditOverflowPolicy.Throw
        });

        using var dispatcher = new InProcessRequestAuditDispatcher(
            scopeFactory.Object,
            options,
            NullLogger<InProcessRequestAuditDispatcher>.Instance,
            deadLetterQueue: null,
            diagnostics: diagnostics);

        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "first" });
        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "second" });

        Assert.That(
            async () => await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "overflow" }),
            Throws.TypeOf<TimeoutException>());

        Assert.That(diagnostics.RequestDispatcherEnqueueTimeoutCount, Is.EqualTo(1));
        Assert.That(diagnostics.RequestDispatcherDlqRoutedCount, Is.Zero);
    }

    [Test]
    public async Task DispatchAsync_RouteToDeadLetter_IncrementsDlqRoutedCounter()
    {
        var diagnostics = new AuditDiagnostics();
        var capturingDlq = new CapturingDeadLetterQueue();
        var scopeFactory = new Mock<IServiceScopeFactory>();

        var options = Options.Create(new AuditMiddlewareOptions
        {
            QueueCapacity = 2,
            EnqueueTimeout = TimeSpan.Zero,
            OverflowPolicy = RequestAuditOverflowPolicy.RouteToDeadLetter
        });

        using var dispatcher = new InProcessRequestAuditDispatcher(
            scopeFactory.Object,
            options,
            NullLogger<InProcessRequestAuditDispatcher>.Instance,
            capturingDlq,
            diagnostics);

        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "first" });
        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "second" });
        await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = "overflow" });

        Assert.That(diagnostics.RequestDispatcherEnqueueTimeoutCount, Is.EqualTo(1));
        Assert.That(diagnostics.RequestDispatcherDlqRoutedCount, Is.EqualTo(1));
    }

    [Test]
    public async Task StopAsync_WithMainLoopAndDrainLoopInFlight_RoutesAllEventsToDeadLetter()
    {
        var capturingDlq = new CapturingDeadLetterQueue();
        var diagnostics = new AuditDiagnostics();
        var blockingProcessor = new BlockingProcessor();

        var services = new ServiceCollection();
        services.AddSingleton<IRequestAuditProcessor>(blockingProcessor);
        await using var serviceProvider = services.BuildServiceProvider();
        var scopeFactory = serviceProvider.GetRequiredService<IServiceScopeFactory>();

        var options = Options.Create(new AuditMiddlewareOptions
        {
            QueueCapacity = 16,
            EnqueueTimeout = TimeSpan.Zero,
            DrainTimeout = TimeSpan.FromMilliseconds(50),
            OverflowPolicy = RequestAuditOverflowPolicy.Throw
        });

        using var dispatcher = new InProcessRequestAuditDispatcher(
            scopeFactory,
            options,
            NullLogger<InProcessRequestAuditDispatcher>.Instance,
            capturingDlq,
            diagnostics);

        const int n = 4;
        for (int i = 0; i < n; i++)
        {
            await dispatcher.DispatchAsync(new AuditEvent { CorrelationId = $"event-{i}" });
        }

        await dispatcher.StartAsync(CancellationToken.None);
        await blockingProcessor.FirstInvocationStarted.Task;
        await dispatcher.StopAsync(CancellationToken.None);

        Assert.That(capturingDlq.StoredEvents, Has.Count.EqualTo(n));
        Assert.That(diagnostics.RequestDispatcherShutdownDrainCount, Is.EqualTo(n));
    }

    private sealed class BlockingProcessor : IRequestAuditProcessor
    {
        public TaskCompletionSource FirstInvocationStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ProcessAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
        {
            FirstInvocationStarted.TrySetResult();
            return Task.Delay(TimeSpan.FromHours(1), cancellationToken);
        }
    }

    private sealed class CapturingDeadLetterQueue : IAuditDeadLetterQueue
    {
        public List<AuditEvent> StoredEvents { get; } = new();

        public Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
        {
            StoredEvents.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null, string? reason = null)
            => throw new NotImplementedException();
        public Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
            => throw new NotImplementedException();
        public Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate, DateTimeOffset endDate)
            => throw new NotImplementedException();
        public Task<bool> ReprocessEventAsync(string deadLetterId)
            => throw new NotImplementedException();
        public Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default)
            => throw new NotImplementedException();
        public Task<int> PurgeProcessedEventsAsync()
            => throw new NotImplementedException();
        public Task<DeadLetterStatistics> GetStatisticsAsync()
            => throw new NotImplementedException();
    }
}
