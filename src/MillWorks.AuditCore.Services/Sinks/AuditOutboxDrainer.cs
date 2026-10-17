using System.Diagnostics;
using MillWorks.AuditCore.Abstractions.Dto;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Recovery;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Background service that drains pending outbox rows through <see cref="IAuditBatchProcessor"/>.
/// Handles orchestration only: claim, process, apply outcomes, lease recovery.
/// </summary>
public sealed class AuditOutboxDrainer(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditOutboxDrainer> logger,
    IOptions<SecurityOptions> options,
    AuditOutboxQueueObserver queueObserver)
    : BackgroundService
{
    private const string _leaderLockName = "AuditOutboxDrainer:Leader";

    private DateTimeOffset _lastLeaseRecoveryTime = DateTimeOffset.MinValue;
    private DateTimeOffset _lastQueueSampleTime = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        if (opts.AuditSinkMode != AuditSinkMode.TransactionalOutbox)
        {
            logger.LogInformation("AuditOutboxDrainer disabled — AuditSinkMode is not TransactionalOutbox");
            return;
        }

        logger.LogInformation(
            "AuditOutboxDrainer starting (poll={Poll}ms, batch={Batch}, maxAttempts={MaxAttempts})",
            opts.OutboxDrainerPollInterval.TotalMilliseconds, opts.OutboxDrainerBatchSize,
            opts.OutboxDrainerMaxAttempts);

        var consecutiveFailures = 0;
        var consecutiveContractFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            // Sample queue depth outside the leader lock so every instance keeps the
            // gauges fresh — AcquireLockAsync throws on non-leaders, which would otherwise
            // starve their gauges. Has its own scope and error handling so a sampling
            // failure never trips the drain circuit breaker.
            await SampleQueueDepthIfDueAsync(opts, stoppingToken);

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var lockService = scope.ServiceProvider.GetRequiredService<IAuditDistributedLockService>();
                var lockTtl = TimeSpan.FromSeconds(Math.Max(60, opts.OutboxDrainerPollInterval.TotalSeconds * 3));

                using var lockHandle = await lockService.AcquireLockAsync(_leaderLockName, lockTtl, stoppingToken);

                var now = DateTimeOffset.UtcNow;
                if (now - _lastLeaseRecoveryTime >= opts.OutboxDrainerLeaseRecoveryInterval)
                {
                    await RecoverExpiredLeasesAsync(scope.ServiceProvider, stoppingToken);
                    _lastLeaseRecoveryTime = now;
                }

                var processed = await DrainBatchAsync(scope.ServiceProvider, stoppingToken);
                await scope.ServiceProvider.GetRequiredService<OutboxHandoffDispatcher>().DispatchAsync(
                    Math.Min(opts.OutboxDrainerBatchSize, OutboxStateStore.MaxBatchSize), opts.OutboxDrainerLeaseDuration, stoppingToken);
                if (processed > 0)
                {
                    consecutiveContractFailures = 0;
                    consecutiveFailures = 0;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested) { break; }
            catch (AuditBatchContractException ex)
            {
                consecutiveContractFailures++;
                logger.LogCritical(ex, "Outbox result contract failed ({Count}/{Limit}); claims remain recoverable",
                    consecutiveContractFailures, Math.Max(1, opts.OutboxDrainerCircuitBreakerThreshold));
                if (consecutiveContractFailures >= Math.Max(1, opts.OutboxDrainerCircuitBreakerThreshold)) throw;
            }
            catch (Exception ex)
            {
                consecutiveFailures++;
                logger.LogError(ex, "Outbox drainer cycle failed ({Consecutive} consecutive failures)", consecutiveFailures);

                if (consecutiveFailures >= opts.OutboxDrainerCircuitBreakerThreshold)
                {
                    logger.LogWarning(
                        "Circuit breaker open — sleeping {Sleep}s after {Threshold} consecutive failures",
                        opts.OutboxDrainerCircuitBreakerSleep.TotalSeconds, opts.OutboxDrainerCircuitBreakerThreshold);

                    try { await Task.Delay(opts.OutboxDrainerCircuitBreakerSleep, stoppingToken); }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }

                    consecutiveFailures = opts.OutboxDrainerCircuitBreakerThreshold - 1;
                }
            }

            try { await Task.Delay(opts.OutboxDrainerPollInterval, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }

        logger.LogInformation("AuditOutboxDrainer stopped");
    }

    internal async Task<int> DrainBatchAsync(IServiceProvider sp, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var opts = options.Value;
        var state = sp.GetRequiredService<OutboxStateStore>();
        var claimed = await state.ClaimAsync(Guid.NewGuid(), Math.Min(opts.OutboxDrainerBatchSize,
            OutboxStateStore.MaxBatchSize), opts.OutboxDrainerLeaseDuration, ct);
        if (claimed.Claims.Length == 0) return 0;
        var valid = new List<ClaimedOutboxRow>();
        var changes = new List<OutboxChange>();
        foreach (var row in claimed.Claims)
        {
            try
            {
                valid.Add(new ClaimedOutboxRow
                {
                    RowId = row.Id, Envelope = QueuedEnvelopeValidator.Read(row.EnvelopeJson, row.EnvelopeVersion),
                    AttemptCount = row.AttemptCount, CreatedAt = row.CreatedAt
                });
            }
            catch (InvalidDataException ex)
            {
                changes.Add(new(row.Id, row.Token, AuditOutboxStatus.Failed, ReasonCode: ex.Message));
            }
        }
        if (valid.Count != 0)
        {
            var result = await sp.GetRequiredService<IAuditBatchProcessor>().ProcessBatchAsync(valid, ct);
            var expected = valid.Select(x => x.RowId).ToHashSet();
            if (result?.Outcomes is null || result.Outcomes.Any(x => x is null) || result.Outcomes.Count != expected.Count || result.Outcomes.Select(x => x.RowId).Distinct().Count() != expected.Count ||
                result.Outcomes.Any(x => !expected.Contains(x.RowId) || !Enum.IsDefined(x.Status) ||
                    (x.Status == RowStatus.RetryLater) != x.IsRetryable ||
                    (x.Status != RowStatus.RetryLater && x.RetryAfter is not null) ||
                    (x.Status is RowStatus.Succeeded or RowStatus.Duplicate && (x.Exception is not null || x.ErrorMessage is not null))))
                throw new AuditBatchContractException("Processor must return exactly one outcome per claimed input row.");
            var rows = claimed.Claims.ToDictionary(x => x.Id);
            foreach (var outcome in result.Outcomes)
            {
                var row = rows[outcome.RowId];
                var status = outcome.Status switch
                {
                    RowStatus.Succeeded or RowStatus.Duplicate => AuditOutboxStatus.Completed,
                    RowStatus.RetryLater when row.AttemptCount + 1 < opts.OutboxDrainerMaxAttempts => AuditOutboxStatus.Pending,
                    _ => AuditOutboxStatus.Failed
                };
                var next = status == AuditOutboxStatus.Pending
                    ? DateTimeOffset.UtcNow + (outcome.RetryAfter ?? GetBackoffWithJitter(row.AttemptCount, opts))
                    : (DateTimeOffset?)null;
                var reason = outcome.Exception is Identity.AuditIdentityConflictException conflict
                    ? conflict.Unverified ? "audit_identity_unverified" : "audit_identity_conflict"
                    : "write_failed";
                changes.Add(new(row.Id, row.Token, status, next, reason));
            }
        }
        ct.ThrowIfCancellationRequested();
        var applied = await state.ApplyAsync(Guid.NewGuid(), changes, ct);
        AuditMetrics.OutboxDrainDuration.Record(stopwatch.Elapsed.TotalMilliseconds);
        logger.LogDebug("Applied {Applied} outbox outcomes; {Rejected} claims no longer owned",
            applied.Applied.Length, applied.Rejected.Length);
        return applied.Applied.Length;
    }

    private async Task RecoverExpiredLeasesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var result = await sp.GetRequiredService<OutboxStateStore>().RecoverAsync(Guid.NewGuid(),
            Math.Min(options.Value.OutboxDrainerBatchSize, OutboxStateStore.MaxBatchSize), ct);
        AuditMetrics.LeasesRecovered.Add(result.Applied.Length);
    }

    private async Task SampleQueueDepthIfDueAsync(SecurityOptions opts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastQueueSampleTime < opts.OutboxQueueDepthSampleInterval)
            return;

        // Advance the timestamp before the query so a persistently failing sample backs
        // off for the full interval instead of hammering the database every poll.
        _lastQueueSampleTime = now;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var auditCtx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            var (pending, inFlight, oldestAgeSeconds) = await ComputeQueueDepthAsync(auditCtx, now, ct);
            queueObserver.Update(pending, inFlight, oldestAgeSeconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — nothing to report.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to sample outbox queue depth for metrics");
        }
    }

    /// <summary>
    /// Computes the queue-depth gauge values from the outbox table: pending row count,
    /// in-flight row count, and the age (seconds) of the oldest pending row (0 if none).
    /// All three queries are index-backed aggregates that track no entities.
    /// </summary>
    internal static async Task<(long Pending, long InFlight, long OldestPendingAgeSeconds)> ComputeQueueDepthAsync(
        AuditDbContext auditCtx, DateTimeOffset now, CancellationToken ct)
    {
        var pending = await auditCtx.AuditOutbox
            .CountAsync(static o => o.Status == AuditOutboxStatus.Pending, ct);

        var inFlight = await auditCtx.AuditOutbox
            .CountAsync(static o => o.Status == AuditOutboxStatus.InFlight, ct);

        var oldestPendingCreatedAt = await auditCtx.AuditOutbox
            .Where(static o => o.Status == AuditOutboxStatus.Pending)
            .OrderBy(static o => o.CreatedAt)
            .Select(static o => (DateTimeOffset?)o.CreatedAt)
            .FirstOrDefaultAsync(ct);

        var oldestAgeSeconds = oldestPendingCreatedAt is { } createdAt
            ? (long)Math.Max(0, (now - createdAt).TotalSeconds)
            : 0L;

        return (pending, inFlight, oldestAgeSeconds);
    }

    private static TimeSpan GetBackoffWithJitter(int attemptIndex, SecurityOptions opts)
    {
        var schedule = opts.OutboxDrainerRetryBackoff;
        var baseBackoff = attemptIndex < schedule.Length ? schedule[attemptIndex] : schedule[^1];
        var jitter = opts.OutboxDrainerBackoffJitterRatio;
        if (jitter <= 0) return baseBackoff;

        var jitterRange = baseBackoff.TotalMilliseconds * jitter;
        var offset = (Random.Shared.NextDouble() * 2 - 1) * jitterRange;
        return TimeSpan.FromMilliseconds(Math.Max(0, baseBackoff.TotalMilliseconds + offset));
    }
}
