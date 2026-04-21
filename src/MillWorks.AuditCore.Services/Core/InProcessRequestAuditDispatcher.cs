using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Default in-process dispatcher for deferred HTTP request audit events.
/// </summary>
public sealed class InProcessRequestAuditDispatcher : BackgroundService, IRequestAuditDispatcher
{
    private readonly Channel<AuditEvent> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<InProcessRequestAuditDispatcher> _logger;
    private readonly TimeSpan _enqueueTimeout;
    private readonly TimeSpan _drainTimeout;
    private readonly IAuditDeadLetterQueue? _deadLetterQueue;
    private readonly RequestAuditOverflowPolicy _overflowPolicy;
    private readonly IAuditDiagnostics? _diagnostics;

    public InProcessRequestAuditDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditMiddlewareOptions> options,
        ILogger<InProcessRequestAuditDispatcher> logger,
        IAuditDeadLetterQueue? deadLetterQueue = null,
        IAuditDiagnostics? diagnostics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _deadLetterQueue = deadLetterQueue;
        _diagnostics = diagnostics;

        var resolvedOptions = options.Value;
        var queueCapacity = resolvedOptions.QueueCapacity <= 0 ? 1000 : resolvedOptions.QueueCapacity;
        _enqueueTimeout = resolvedOptions.EnqueueTimeout;
        _drainTimeout = resolvedOptions.DrainTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : resolvedOptions.DrainTimeout;
        _overflowPolicy = resolvedOptions.OverflowPolicy;

        _channel = Channel.CreateBounded<AuditEvent>(new BoundedChannelOptions(queueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    /// <inheritdoc />
    public async ValueTask DispatchAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (_enqueueTimeout == TimeSpan.Zero)
        {
            if (_channel.Writer.TryWrite(auditEvent))
                return;

            _diagnostics?.Increment(AuditDiagnosticCounter.RequestDispatcherEnqueueTimeout);
            await HandleOverflowAsync(auditEvent, exception: null);

            if (_overflowPolicy == RequestAuditOverflowPolicy.Throw)
                throw new TimeoutException("Deferred request audit queue is full.");

            return;
        }

        using var enqueueCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enqueueCts.CancelAfter(_enqueueTimeout);

        try
        {
            await _channel.Writer.WriteAsync(auditEvent, enqueueCts.Token);
        }
        catch (ChannelClosedException cce)
        {
            _diagnostics?.Increment(AuditDiagnosticCounter.RequestDispatcherEnqueueTimeout);
            await HandleOverflowAsync(auditEvent, cce);

            if (_overflowPolicy == RequestAuditOverflowPolicy.Throw)
                throw;
        }
        catch (OperationCanceledException oce) when (!cancellationToken.IsCancellationRequested)
        {
            _diagnostics?.Increment(AuditDiagnosticCounter.RequestDispatcherEnqueueTimeout);
            await HandleOverflowAsync(auditEvent, oce);

            if (_overflowPolicy == RequestAuditOverflowPolicy.Throw)
                throw;
        }
    }

    private async Task HandleOverflowAsync(AuditEvent auditEvent, Exception? exception)
    {
        switch (_overflowPolicy)
        {
            case RequestAuditOverflowPolicy.Throw:
                return;

            case RequestAuditOverflowPolicy.DropAndLog:
                _logger.LogWarning(exception,
                    "Deferred request audit queue overflow; event {EventId} with correlation id {CorrelationId} dropped under overflow policy {OverflowPolicy}",
                    auditEvent.EventId, auditEvent.CorrelationId, _overflowPolicy);
                return;

            case RequestAuditOverflowPolicy.RouteToDeadLetter:
                if (_deadLetterQueue is null)
                {
                    _logger.LogWarning(exception,
                        "Deferred request audit queue overflow; event {EventId} with correlation id {CorrelationId} dropped under overflow policy {OverflowPolicy} because no dead letter queue is registered",
                        auditEvent.EventId, auditEvent.CorrelationId, _overflowPolicy);
                    return;
                }

                try
                {
                    await _deadLetterQueue.StoreFailedEventAsync(auditEvent, exception, "Request audit queue overflow");
                    _diagnostics?.Increment(AuditDiagnosticCounter.RequestDispatcherDlqRouted);
                    _logger.LogWarning(exception,
                        "Deferred request audit queue overflow; event {EventId} with correlation id {CorrelationId} routed to dead letter queue under overflow policy {OverflowPolicy}",
                        auditEvent.EventId, auditEvent.CorrelationId, _overflowPolicy);
                }
                catch (Exception dlqEx)
                {
                    _logger.LogError(dlqEx,
                        "Failed to route audit event {EventId} with correlation id {CorrelationId} to dead letter queue under overflow policy {OverflowPolicy}",
                        auditEvent.EventId, auditEvent.CorrelationId, _overflowPolicy);
                }
                return;
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InProcessRequestAuditDispatcher started");

        AuditEvent? mainLoopInFlight = null;

        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_channel.Reader.TryRead(out var auditEvent))
                {
                    mainLoopInFlight = auditEvent;
                    await ProcessOneAsync(auditEvent, stoppingToken);
                    mainLoopInFlight = null;
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (mainLoopInFlight is not null)
            {
                await RouteShutdownDrainAsync(mainLoopInFlight);
            }
        }
        finally
        {
            using var drainCts = new CancellationTokenSource(_drainTimeout);
            var drainTimedOut = false;

            while (_channel.Reader.TryRead(out var remaining))
            {
                if (drainTimedOut)
                {
                    await RouteShutdownDrainAsync(remaining);
                    continue;
                }

                try
                {
                    await ProcessOneAsync(remaining, drainCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Shutdown drain timed out after {DrainTimeoutMs} ms; routing remainder to dead letter queue",
                        _drainTimeout.TotalMilliseconds);
                    drainTimedOut = true;
                    await RouteShutdownDrainAsync(remaining);
                }
            }

            _logger.LogInformation("InProcessRequestAuditDispatcher stopped");
        }
    }

    private async Task RouteShutdownDrainAsync(AuditEvent auditEvent)
    {
        _diagnostics?.Increment(AuditDiagnosticCounter.RequestDispatcherShutdownDrain);

        if (_deadLetterQueue is null)
        {
            _logger.LogWarning(
                "Shutdown drain: event {EventId} with correlation id {CorrelationId} dropped because no dead letter queue is registered",
                auditEvent.EventId, auditEvent.CorrelationId);
            return;
        }

        try
        {
            await _deadLetterQueue.StoreFailedEventAsync(auditEvent, exception: null, "Shutdown drain");
            _logger.LogWarning(
                "Shutdown drain: event {EventId} with correlation id {CorrelationId} routed to dead letter queue",
                auditEvent.EventId, auditEvent.CorrelationId);
        }
        catch (Exception dlqEx)
        {
            _logger.LogError(dlqEx,
                "Shutdown drain: failed to route event {EventId} with correlation id {CorrelationId} to dead letter queue",
                auditEvent.EventId, auditEvent.CorrelationId);
        }
    }

    private async Task ProcessOneAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IRequestAuditProcessor>();
        await processor.ProcessAsync(auditEvent, cancellationToken);
    }
}
