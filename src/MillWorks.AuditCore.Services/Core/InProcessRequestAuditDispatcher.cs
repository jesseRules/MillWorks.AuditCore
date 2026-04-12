using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Models;
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

    public InProcessRequestAuditDispatcher(
        IServiceScopeFactory scopeFactory,
        IOptions<AuditMiddlewareOptions> options,
        ILogger<InProcessRequestAuditDispatcher> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var resolvedOptions = options.Value;
        var queueCapacity = resolvedOptions.QueueCapacity <= 0 ? 1000 : resolvedOptions.QueueCapacity;
        _enqueueTimeout = resolvedOptions.EnqueueTimeout;
        _drainTimeout = resolvedOptions.DrainTimeout <= TimeSpan.Zero
            ? TimeSpan.FromSeconds(30)
            : resolvedOptions.DrainTimeout;

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
            if (!_channel.Writer.TryWrite(auditEvent))
                throw new TimeoutException("Deferred request audit queue is full.");

            return;
        }

        using var enqueueCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        enqueueCts.CancelAfter(_enqueueTimeout);
        await _channel.Writer.WriteAsync(auditEvent, enqueueCts.Token);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InProcessRequestAuditDispatcher started");

        try
        {
            while (await _channel.Reader.WaitToReadAsync(stoppingToken))
            {
                while (_channel.Reader.TryRead(out var auditEvent))
                {
                    await ProcessOneAsync(auditEvent, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Graceful shutdown. Remaining items are handled below.
        }
        finally
        {
            using var drainCts = new CancellationTokenSource(_drainTimeout);

            while (_channel.Reader.TryRead(out var remaining))
            {
                try
                {
                    await ProcessOneAsync(remaining, drainCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "InProcessRequestAuditDispatcher stopped draining queued request audits after {DrainTimeoutMs} ms",
                        _drainTimeout.TotalMilliseconds);
                    break;
                }
            }

            _logger.LogInformation("InProcessRequestAuditDispatcher stopped");
        }
    }

    private async Task ProcessOneAsync(AuditEvent auditEvent, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var processor = scope.ServiceProvider.GetRequiredService<IRequestAuditProcessor>();
        await processor.ProcessAsync(auditEvent, cancellationToken);
    }
}
