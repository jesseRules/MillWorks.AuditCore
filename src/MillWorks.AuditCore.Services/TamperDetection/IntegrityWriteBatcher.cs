using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Background service that batches integrity record writes to reduce per-event lock contention
/// and database round-trips. Events are enqueued via <see cref="EnqueueAsync"/> and flushed
/// either when the batch is full or when the flush interval elapses, whichever comes first.
///
/// <para><b>Crash semantics:</b> On graceful shutdown, all queued
/// records are flushed before the service exits. On hard kill, pending records are lost —
/// the audit events themselves are already persisted, but their integrity records may be
/// missing. A chain verification pass will detect and report these gaps.</para>
///
/// <para><b>Callers:</b> <see cref="TamperDetectionService"/> routes through this service
/// when <see cref="SecurityOptions.EnableBatchedIntegrityWrites"/> is true.</para>
/// </summary>
public sealed class IntegrityWriteBatcher : BackgroundService
{
    private readonly Channel<PendingIntegrityWrite> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IntegrityWriteBatcher> _logger;
    private readonly IAuditDiagnostics? _diagnostics;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    /// <summary>
    /// Represents a pending integrity write with its completion signal.
    /// </summary>
    internal readonly record struct PendingIntegrityWrite(
        AuditIntegrityDto Event,
        TaskCompletionSource<AuditIntegrityDto> Completion);

    public IntegrityWriteBatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<IntegrityWriteBatcher> logger,
        SecurityOptions securityOptions,
        IAuditDiagnostics? diagnostics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _diagnostics = diagnostics;
        _batchSize = securityOptions.IntegrityBatchSize;
        _flushInterval = securityOptions.IntegrityFlushInterval;

        // Bounded channel prevents unbounded memory growth under sustained backpressure.
        // 10x batch size gives enough runway without excessive memory use.
        _channel = Channel.CreateBounded<PendingIntegrityWrite>(new BoundedChannelOptions(_batchSize * 10)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true
        });
    }

    /// <summary>
    /// Enqueues an integrity record for batched writing. Returns when the record
    /// has been successfully persisted (not just enqueued).
    /// </summary>
    public async Task<AuditIntegrityDto> EnqueueAsync(
        AuditIntegrityDto auditEvent,
        CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<AuditIntegrityDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingIntegrityWrite(auditEvent, tcs);

        await _channel.Writer.WriteAsync(pending, cancellationToken);

        // Wait for the batch flush to complete this specific write
        return await tcs.Task;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IntegrityWriteBatcher started (batchSize={BatchSize}, flushInterval={FlushMs}ms)",
            _batchSize, _flushInterval.TotalMilliseconds);

        var batch = new List<PendingIntegrityWrite>(_batchSize);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                batch.Clear();

                // Wait for at least one item
                try
                {
                    var first = await _channel.Reader.ReadAsync(stoppingToken);
                    batch.Add(first);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                // Drain up to batch size with a flush deadline — zero-polling
                using var flushCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                flushCts.CancelAfter(_flushInterval);

                while (batch.Count < _batchSize)
                {
                    // Drain anything already buffered without waiting
                    while (batch.Count < _batchSize && _channel.Reader.TryRead(out var buffered))
                        batch.Add(buffered);

                    if (batch.Count >= _batchSize)
                        break;

                    // Wait for more data OR flush interval expiry
                    try
                    {
                        if (!await _channel.Reader.WaitToReadAsync(flushCts.Token))
                            break; // Channel completed
                    }
                    catch (OperationCanceledException)
                    {
                        break; // Flush interval expired — flush what we have
                    }
                }

                await FlushBatchAsync(batch, stoppingToken);
            }
        }
        finally
        {
            // Graceful shutdown: drain remaining items
            while (_channel.Reader.TryRead(out var remaining))
            {
                batch.Add(remaining);
            }

            if (batch.Count > 0)
            {
                _logger.LogInformation("IntegrityWriteBatcher: flushing {Count} remaining records on shutdown", batch.Count);
                await FlushBatchAsync(batch, CancellationToken.None);
            }

            _logger.LogInformation("IntegrityWriteBatcher stopped");
        }
    }

    private async Task FlushBatchAsync(List<PendingIntegrityWrite> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tamperDetection = scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();

            var events = batch.Select(b => b.Event).ToList();
            var results = await tamperDetection.CreateIntegrityRecordBatchAsync(events, cancellationToken);

            if (results.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"IntegrityWriteBatcher: CreateIntegrityRecordBatchAsync returned {results.Count} " +
                    $"results but {batch.Count} were expected. Failing the entire batch.");
            }

            // Signal success to all callers
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetResult(results[i]);
            }

            _logger.LogDebug("IntegrityWriteBatcher: flushed {Count} records", batch.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntegrityWriteBatcher: batch flush failed for {Count} records", batch.Count);

            // Signal failure to all callers
            foreach (var item in batch)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }
}
