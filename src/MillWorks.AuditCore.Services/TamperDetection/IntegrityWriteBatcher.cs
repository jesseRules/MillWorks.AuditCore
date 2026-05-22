using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Background service that batches integrity record writes to reduce per-event lock contention
/// and database round-trips. Events are enqueued via <see cref="EnqueueAsync"/> and flushed
/// either when the batch is full or when the flush interval elapses, whichever comes first.
///
/// <para><b>Crash semantics:</b> On graceful shutdown, all queued records are flushed before
/// the service exits. On hard kill, the in-memory channel contents are lost, but the durable
/// <c>AuditIntegrityWorkItem</c> outbox (written transactionally with the audit event in
/// <c>AuditLogger</c>) survives. The <c>IntegrityReconciliationService</c> picks up any
/// stale pending work items on startup and on schedule.</para>
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
    private readonly IOptions<SecurityOptions> _options;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private volatile bool _stopping;

    /// <summary>
    /// Represents a pending integrity write with its completion signal.
    /// </summary>
    internal readonly record struct PendingIntegrityWrite(
        AuditIntegrityDto Event,
        TaskCompletionSource<AuditIntegrityDto> Completion);

    public IntegrityWriteBatcher(
        IServiceScopeFactory scopeFactory,
        ILogger<IntegrityWriteBatcher> logger,
        IOptions<SecurityOptions> securityOptions,
        IAuditDiagnostics? diagnostics = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _diagnostics = diagnostics;
        _options = securityOptions;

        var opts = securityOptions.Value;
        _batchSize = opts.IntegrityBatchSize;
        _flushInterval = opts.IntegrityFlushInterval;

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
    /// <exception cref="InvalidOperationException">
    /// Thrown if the batcher is stopping and cannot accept new work.
    /// </exception>
    public async Task<AuditIntegrityDto> EnqueueAsync(
        AuditIntegrityDto auditEvent,
        CancellationToken cancellationToken)
    {
        if (_stopping)
        {
            throw new InvalidOperationException(
                "IntegrityWriteBatcher is stopping and cannot accept new work. " +
                "The audit event will be picked up by IntegrityReconciliationService.");
        }

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.IntegrityWrite,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.AuditEventId, auditEvent.EventId.ToString());

        var tcs = new TaskCompletionSource<AuditIntegrityDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingIntegrityWrite(auditEvent, tcs);

        try
        {
            await _channel.Writer.WriteAsync(pending, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException(
                "IntegrityWriteBatcher is stopping and cannot accept new work. " +
                "The audit event will be picked up by IntegrityReconciliationService.");
        }

        // Wait for the batch flush to complete this specific write
        var result = await tcs.Task;
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");
        return result;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var securityOptions = _options.Value;

        if (!securityOptions.EnableTamperDetection || !securityOptions.EnableBatchedIntegrityWrites)
        {
            _logger.LogInformation("IntegrityWriteBatcher disabled by configuration");
            return;
        }

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

                var flushToken = stoppingToken.IsCancellationRequested
                    ? CancellationToken.None
                    : stoppingToken;

                await FlushBatchAsync(batch, flushToken);
            }
        }
        finally
        {
            // Set stopping flag first to reject new enqueues immediately
            _stopping = true;

            // Complete the channel writer so any WriteAsync in flight throws ChannelClosedException
            _channel.Writer.TryComplete();

            // Graceful shutdown: drain remaining items and attempt to flush
            while (_channel.Reader.TryRead(out var remaining))
            {
                batch.Add(remaining);
            }

            if (batch.Count > 0)
            {
                _logger.LogInformation("IntegrityWriteBatcher: flushing {Count} remaining records on shutdown", batch.Count);

                try
                {
                    await FlushBatchAsync(batch, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    // Flush failed during shutdown — fail all pending TCS so callers don't hang
                    _logger.LogError(ex, "IntegrityWriteBatcher: shutdown flush failed for {Count} records; failing callers", batch.Count);
                    var shutdownException = new InvalidOperationException(
                        "IntegrityWriteBatcher shutdown flush failed. " +
                        "The audit event will be picked up by IntegrityReconciliationService.", ex);
                    foreach (var item in batch)
                    {
                        item.Completion.TrySetException(shutdownException);
                    }
                }
            }

            _logger.LogInformation("IntegrityWriteBatcher stopped");
        }
    }

    private async Task FlushBatchAsync(List<PendingIntegrityWrite> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0) return;

        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.IntegrityFlush,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.BatchSize, batch.Count);

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var tamperDetection = scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            var events = batch.Select(static b => b.Event).ToList();
            var results = await tamperDetection.CreateIntegrityRecordBatchAsync(events, cancellationToken);

            if (results.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"IntegrityWriteBatcher: CreateIntegrityRecordBatchAsync returned {results.Count} " +
                    $"results but {batch.Count} were expected. Failing the entire batch.");
            }

            // Mark work items and events as Completed atomically.
            // Both updates must succeed together to avoid orphaned Pending events
            // that the reconciliation service won't pick up.
            var eventIds = batch.Select(static b => b.Event.EventId).ToList();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                await dbContext.IntegrityWorkItems
                    .Where(w => eventIds.Contains(w.EventId) && w.Status == IntegrityStatus.Pending)
                    .ExecuteUpdateAsync(static s => s
                        .SetProperty(static w => w.Status, IntegrityStatus.Completed)
                        .SetProperty(static w => w.CompletedAt, DateTimeOffset.UtcNow), cancellationToken);

                await dbContext.AuditEvents
                    .Where(e => eventIds.Contains(e.EventId) && e.IntegrityStatus == IntegrityStatus.Pending)
                    .ExecuteUpdateAsync(static s => s
                        .SetProperty(static e => e.IntegrityStatus, IntegrityStatus.Completed), cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx,
                        "IntegrityWriteBatcher: transaction rollback failed after status update error. " +
                        "Original error: {OriginalError}", ex.Message);
                }
                throw;
            }

            // Signal success to all callers
            for (int i = 0; i < batch.Count; i++)
            {
                batch[i].Completion.TrySetResult(results[i]);
            }

            activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, batch.Count);
            activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

            _diagnostics?.Increment(AuditDiagnosticCounter.IntegrityBatchFlush);
            _logger.LogDebug("IntegrityWriteBatcher: flushed {Count} records", batch.Count);
        }
        catch (Exception ex)
        {
            activity?.SetTag(AuditActivitySource.Tags.Outcome, "failure");
            _diagnostics?.Increment(AuditDiagnosticCounter.IntegrityBatchFlushFailure);
            _logger.LogError(ex, "IntegrityWriteBatcher: batch flush failed for {Count} records", batch.Count);

            // Update work items with failure metadata (best-effort — don't let this mask the original error)
            try
            {
                using var failScope = _scopeFactory.CreateScope();
                var failDbContext = failScope.ServiceProvider.GetRequiredService<AuditDbContext>();
                var failedEventIds = batch.Select(static b => b.Event.EventId).ToList();

                await failDbContext.IntegrityWorkItems
                    .Where(w => failedEventIds.Contains(w.EventId) && w.Status == IntegrityStatus.Pending)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(static w => w.AttemptCount, static w => w.AttemptCount + 1)
                        .SetProperty(static w => w.LastAttemptAt, DateTimeOffset.UtcNow)
                        .SetProperty(static w => w.LastError, ex.Message), cancellationToken);
            }
            catch (Exception updateEx)
            {
                _logger.LogWarning(updateEx,
                    "IntegrityWriteBatcher: failed to update work item failure metadata");
            }

            // Signal failure to all callers
            foreach (var item in batch)
            {
                item.Completion.TrySetException(ex);
            }
        }
    }
}
