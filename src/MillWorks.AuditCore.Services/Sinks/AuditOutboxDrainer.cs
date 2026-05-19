using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Background service that drains pending outbox rows and publishes them through
/// <see cref="ImmediateSink"/> to the audit DbContext. Handles retries, circuit
/// breaker, and DLQ routing for permanently failed rows.
/// </summary>
/// <remarks>
/// Leadership is acquired via <see cref="IAuditDistributedLockService"/> — only
/// one drainer instance processes outbox rows at a time per replica set.
/// </remarks>
public sealed class AuditOutboxDrainer : BackgroundService
{
    private const string LeaderLockName = "AuditOutboxDrainer:Leader";
    private static readonly Meter _meter = new("MillWorks.AuditCore.OutboxDrainer", "1.0.0");
    private static readonly Counter<long> _failedCounter = _meter.CreateCounter<long>(
        "audit.outbox.drainer.failed",
        "rows",
        "Number of outbox rows that exhausted retries and were routed to DLQ");

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditOutboxDrainer> _logger;
    private readonly IOptions<SecurityOptions> _options;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public AuditOutboxDrainer(
        IServiceScopeFactory scopeFactory,
        ILogger<AuditOutboxDrainer> logger,
        IOptions<SecurityOptions> options)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = _options.Value;

        if (opts.AuditSinkMode != AuditSinkMode.TransactionalOutbox)
        {
            _logger.LogInformation("AuditOutboxDrainer disabled — AuditSinkMode is not TransactionalOutbox");
            return;
        }

        _logger.LogInformation(
            "AuditOutboxDrainer starting (poll={Poll}ms, batch={Batch}, maxAttempts={MaxAttempts})",
            opts.OutboxDrainerPollInterval.TotalMilliseconds,
            opts.OutboxDrainerBatchSize,
            opts.OutboxDrainerMaxAttempts);

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var lockService = scope.ServiceProvider.GetRequiredService<IAuditDistributedLockService>();

                var lockTtl = TimeSpan.FromSeconds(
                    Math.Max(60, opts.OutboxDrainerPollInterval.TotalSeconds * 3));

                using var lockHandle = await lockService.AcquireLockAsync(
                    LeaderLockName,
                    lockTtl,
                    stoppingToken);

                var processed = await DrainBatchAsync(scope.ServiceProvider, stoppingToken);

                if (processed > 0)
                    consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                _logger.LogError(ex, "Outbox drainer cycle failed ({Consecutive} consecutive failures)",
                    consecutiveFailures);

                if (consecutiveFailures >= opts.OutboxDrainerCircuitBreakerThreshold)
                {
                    _logger.LogWarning(
                        "Circuit breaker open — sleeping {Sleep}s after {Threshold} consecutive failures",
                        opts.OutboxDrainerCircuitBreakerSleep.TotalSeconds,
                        opts.OutboxDrainerCircuitBreakerThreshold);

                    try
                    {
                        await Task.Delay(opts.OutboxDrainerCircuitBreakerSleep, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }

                    consecutiveFailures = opts.OutboxDrainerCircuitBreakerThreshold - 1;
                }
            }

            try
            {
                await Task.Delay(opts.OutboxDrainerPollInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("AuditOutboxDrainer stopped");
    }

    private async Task<int> DrainBatchAsync(IServiceProvider sp, CancellationToken ct)
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxDrain,
            ActivityKind.Internal);

        var opts = _options.Value;
        var auditCtx = sp.GetRequiredService<AuditDbContext>();
        var sink = sp.GetRequiredService<ImmediateSink>();
        var dlq = sp.GetService<IAuditDeadLetterQueue>();

        var now = DateTimeOffset.UtcNow;
        var pending = await auditCtx.AuditOutbox
            .Where(o => o.Status == AuditOutboxStatus.Pending &&
                        (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(static o => o.CreatedAt)
            .Take(opts.OutboxDrainerBatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0)
        {
            activity?.SetTag(AuditActivitySource.Tags.BatchSize, 0);
            return 0;
        }

        activity?.SetTag(AuditActivitySource.Tags.BatchSize, pending.Count);

        var processed = 0;

        foreach (var row in pending)
        {
            try
            {
                if (row.EnvelopeVersion != TransactionalOutboxSink.CurrentEnvelopeVersion)
                {
                    throw new InvalidOperationException(
                        $"Envelope version mismatch: row has v{row.EnvelopeVersion}, " +
                        $"expected v{TransactionalOutboxSink.CurrentEnvelopeVersion}");
                }

                var envelope = JsonSerializer.Deserialize<AuditEnvelope>(row.EnvelopeJson, _jsonOptions)
                    ?? throw new InvalidOperationException("Envelope deserialized to null");

                await sink.PublishAsync(envelope, ct);

                row.Status = AuditOutboxStatus.Completed;
                row.CompletedAt = DateTimeOffset.UtcNow;
                processed++;
            }
            catch (Exception ex)
            {
                row.AttemptCount++;
                row.LastError = ex.Message.Length > 2000
                    ? ex.Message[..2000]
                    : ex.Message;

                if (row.AttemptCount >= opts.OutboxDrainerMaxAttempts)
                {
                    row.Status = AuditOutboxStatus.Failed;
                    _failedCounter.Add(1, new KeyValuePair<string, object?>("stage", "drain"));
                    activity?.AddEvent(new ActivityEvent(
                        AuditActivitySource.Events.OutboxExhausted,
                        tags: new ActivityTagsCollection { { AuditActivitySource.Tags.OutboxRowId, row.Id.ToString() } }));

                    if (dlq is not null)
                    {
                        try
                        {
                            var failedEvent = new AuditEvent
                            {
                                EventType = "OutboxDrainFailed",
                                EntityName = $"AuditOutbox:{row.Id}",
                                Action = Abstractions.Enums.AuditAction.Unknown,
                            };
                            failedEvent.CustomFields["EnvelopeJson"] = row.EnvelopeJson;
                            failedEvent.CustomFields["RowId"] = row.Id;
                            failedEvent.CustomFields["AttemptCount"] = row.AttemptCount;

                            await dlq.StoreFailedEventAsync(
                                failedEvent,
                                ex,
                                $"Outbox drain exhausted {opts.OutboxDrainerMaxAttempts} attempts");
                        }
                        catch (Exception dlqEx)
                        {
                            _logger.LogError(dlqEx,
                                "Failed to enqueue outbox row {RowId} to DLQ after {Attempts} attempts",
                                row.Id, row.AttemptCount);
                        }
                    }

                    _logger.LogWarning(
                        "Outbox row {RowId} marked Failed after {Attempts} attempts: {Error}",
                        row.Id, row.AttemptCount, ex.Message);
                }
                else
                {
                    var backoff = GetBackoffWithJitter(row.AttemptCount - 1, opts);
                    row.NextRetryAt = DateTimeOffset.UtcNow.Add(backoff);
                    _logger.LogWarning(ex,
                        "Outbox row {RowId} attempt {Attempt} failed, will retry after {Backoff}s at {NextRetryAt:O}",
                        row.Id, row.AttemptCount, backoff.TotalSeconds, row.NextRetryAt);
                }
            }
        }

        await auditCtx.SaveChangesAsync(ct);

        activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, processed);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

        _logger.LogDebug("Drained {Processed}/{Total} outbox rows", processed, pending.Count);
        return processed;
    }

    private static TimeSpan GetBackoffWithJitter(int attemptIndex, SecurityOptions opts)
    {
        var schedule = opts.OutboxDrainerRetryBackoff;
        var baseBackoff = attemptIndex < schedule.Length
            ? schedule[attemptIndex]
            : schedule[^1];

        var jitter = opts.OutboxDrainerBackoffJitterRatio;
        if (jitter <= 0)
            return baseBackoff;

        var jitterRange = baseBackoff.TotalMilliseconds * jitter;
        var offset = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
        return TimeSpan.FromMilliseconds(Math.Max(0, baseBackoff.TotalMilliseconds + offset));
    }
}
