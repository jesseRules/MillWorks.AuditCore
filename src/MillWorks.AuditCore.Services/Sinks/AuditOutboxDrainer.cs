using System.Diagnostics;
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
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Telemetry;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>
/// Background service that drains pending outbox rows through <see cref="IAuditBatchProcessor"/>.
/// Handles orchestration only: claim, process, apply outcomes, lease recovery.
/// </summary>
public sealed class AuditOutboxDrainer(
    IServiceScopeFactory scopeFactory,
    ILogger<AuditOutboxDrainer> logger,
    IOptions<SecurityOptions> options)
    : BackgroundService
{
    private const string _leaderLockName = "AuditOutboxDrainer:Leader";

    private readonly string _leaseOwnerId = GenerateLeaseOwnerId();
    private DateTimeOffset _lastLeaseRecoveryTime = DateTimeOffset.MinValue;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static string GenerateLeaseOwnerId()
    {
        const int maxLength = 100;
        const int suffixLength = 8;
        const int separatorCount = 2;

        var hostname = Environment.MachineName;
        var pid = Environment.ProcessId.ToString();
        var suffix = Guid.NewGuid().ToString("N")[..suffixLength];

        var hostnameMaxLength = maxLength - pid.Length - suffixLength - separatorCount;
        if (hostname.Length > hostnameMaxLength)
            hostname = hostname[..hostnameMaxLength];

        return $"{hostname}:{pid}:{suffix}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;

        if (opts.AuditSinkMode != AuditSinkMode.TransactionalOutbox)
        {
            logger.LogInformation("AuditOutboxDrainer disabled — AuditSinkMode is not TransactionalOutbox");
            return;
        }

        logger.LogInformation(
            "AuditOutboxDrainer starting (poll={Poll}ms, batch={Batch}, maxAttempts={MaxAttempts}, leaseOwner={LeaseOwner})",
            opts.OutboxDrainerPollInterval.TotalMilliseconds, opts.OutboxDrainerBatchSize,
            opts.OutboxDrainerMaxAttempts, _leaseOwnerId);

        var consecutiveFailures = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
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

    private async Task<int> DrainBatchAsync(IServiceProvider sp, CancellationToken ct)
    {
        var drainStopwatch = Stopwatch.StartNew();
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.OutboxDrain, ActivityKind.Internal);

        var opts = options.Value;
        var auditCtx = sp.GetRequiredService<AuditDbContext>();
        var processor = sp.GetRequiredService<IAuditBatchProcessor>();
        var dlq = sp.GetService<IAuditDeadLetterQueue>();

        var claimedRows = await ClaimBatchAsync(auditCtx, opts, ct);
        if (claimedRows.Count == 0)
        {
            activity?.SetTag(AuditActivitySource.Tags.BatchSize, 0);
            return 0;
        }

        activity?.SetTag(AuditActivitySource.Tags.BatchSize, claimedRows.Count);

        var (validRows, invalidRows) = DeserializeClaimedRows(claimedRows);

        foreach (var (row, ex) in invalidRows)
            await ApplyFailedOutcomeAsync(row, ex.Message, dlq, opts, activity);

        if (validRows.Count > 0)
        {
            var result = await processor.ProcessBatchAsync(validRows, ct);
            await ApplyOutcomesAsync(claimedRows, result.Outcomes, dlq, opts, activity);
        }

        await auditCtx.SaveChangesAsync(ct);

        drainStopwatch.Stop();
        AuditMetrics.OutboxDrainDuration.Record(drainStopwatch.Elapsed.TotalMilliseconds);

        var processed = validRows.Count;
        activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, processed);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");
        logger.LogDebug("Drained {Processed}/{Total} outbox rows ({InvalidCount} invalid)",
            processed, claimedRows.Count, invalidRows.Count);

        return processed;
    }

    private (List<ClaimedOutboxRow> valid, List<(AuditOutboxEntity row, Exception ex)> invalid) DeserializeClaimedRows(
        List<AuditOutboxEntity> rows)
    {
        var valid = new List<ClaimedOutboxRow>(rows.Count);
        var invalid = new List<(AuditOutboxEntity, Exception)>();

        foreach (var row in rows)
        {
            try
            {
                if (row.EnvelopeVersion != TransactionalOutboxSink.CurrentEnvelopeVersion)
                    throw new InvalidOperationException(
                        $"Envelope version mismatch: row has v{row.EnvelopeVersion}, expected v{TransactionalOutboxSink.CurrentEnvelopeVersion}");

                var envelope = JsonSerializer.Deserialize<AuditEnvelope>(row.EnvelopeJson, _jsonOptions)
                    ?? throw new InvalidOperationException("Envelope deserialized to null");

                valid.Add(new ClaimedOutboxRow
                {
                    RowId = row.Id,
                    Envelope = envelope,
                    AttemptCount = row.AttemptCount,
                    CreatedAt = row.CreatedAt
                });
            }
            catch (Exception ex)
            {
                invalid.Add((row, ex));
            }
        }

        return (valid, invalid);
    }

    private async Task ApplyOutcomesAsync(
        List<AuditOutboxEntity> allRows,
        IReadOnlyList<RowOutcome> outcomes,
        IAuditDeadLetterQueue? dlq,
        SecurityOptions opts,
        Activity? activity)
    {
        var rowLookup = allRows.ToDictionary(static r => r.Id);

        foreach (var outcome in outcomes)
        {
            if (!rowLookup.TryGetValue(outcome.RowId, out var row))
            {
                logger.LogError("RowOutcome for unknown RowId {RowId}", outcome.RowId);
                continue;
            }

            switch (outcome.Status)
            {
                case RowStatus.Succeeded:
                case RowStatus.Duplicate:
                    ApplySuccessOutcome(row, outcome.Status == RowStatus.Duplicate);
                    break;

                case RowStatus.RetryLater:
                    await ApplyRetryOutcomeAsync(row, outcome, dlq, opts, activity);
                    break;

                case RowStatus.Failed:
                    await ApplyFailedOutcomeAsync(row, outcome.ErrorMessage ?? "Unknown error", dlq, opts, activity);
                    break;
            }
        }
    }

    private void ApplySuccessOutcome(AuditOutboxEntity row, bool isDuplicate)
    {
        row.Status = AuditOutboxStatus.Completed;
        row.CompletedAt = DateTimeOffset.UtcNow;
        row.LeaseOwner = null;
        row.LeaseExpiresAt = null;

        if (isDuplicate)
            logger.LogDebug("Row {RowId} completed as duplicate", row.Id);
    }

    private async Task ApplyRetryOutcomeAsync(
        AuditOutboxEntity row,
        RowOutcome outcome,
        IAuditDeadLetterQueue? dlq,
        SecurityOptions opts,
        Activity? activity)
    {
        row.AttemptCount++;
        row.LastError = TruncateError(outcome.ErrorMessage);

        if (row.AttemptCount >= opts.OutboxDrainerMaxAttempts)
        {
            await ApplyExhaustedOutcomeAsync(row, outcome.ErrorMessage ?? "Unknown error", dlq, opts, activity);
            return;
        }

        row.Status = AuditOutboxStatus.Pending;
        row.LeaseOwner = null;
        row.LeaseExpiresAt = null;

        var backoff = outcome.RetryAfter ?? GetBackoffWithJitter(row.AttemptCount - 1, opts);
        row.NextRetryAt = DateTimeOffset.UtcNow.Add(backoff);

        logger.LogWarning(
            "Outbox row {RowId} attempt {Attempt}/{MaxAttempts} failed, will retry after {Backoff}s: {Error}",
            row.Id, row.AttemptCount, opts.OutboxDrainerMaxAttempts, backoff.TotalSeconds, outcome.ErrorMessage);
    }

    private async Task ApplyFailedOutcomeAsync(
        AuditOutboxEntity row,
        string errorMessage,
        IAuditDeadLetterQueue? dlq,
        SecurityOptions opts,
        Activity? activity)
    {
        row.AttemptCount++;
        row.LastError = TruncateError(errorMessage);
        await ApplyExhaustedOutcomeAsync(row, errorMessage, dlq, opts, activity);
    }

    private async Task ApplyExhaustedOutcomeAsync(
        AuditOutboxEntity row,
        string errorMessage,
        IAuditDeadLetterQueue? dlq,
        SecurityOptions opts,
        Activity? activity)
    {
        row.Status = AuditOutboxStatus.Failed;
        row.LeaseOwner = null;
        row.LeaseExpiresAt = null;

        AuditMetrics.DlqRouted.Add(1);
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
                    failedEvent, new InvalidOperationException(errorMessage),
                    $"Outbox drain exhausted {opts.OutboxDrainerMaxAttempts} attempts");
            }
            catch (Exception dlqEx)
            {
                logger.LogError(dlqEx, "Failed to enqueue row {RowId} to DLQ", row.Id);
            }
        }

        logger.LogWarning("Outbox row {RowId} marked Failed after {Attempts} attempts: {Error}",
            row.Id, row.AttemptCount, errorMessage);
    }

    private static string? TruncateError(string? error) =>
        error?.Length > 2000 ? SensitiveContentSanitizer.TruncateSafe(error, 2000) : error;

    private async Task<List<AuditOutboxEntity>> ClaimBatchAsync(
        AuditDbContext auditCtx, SecurityOptions opts, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseExpiry = now.Add(opts.OutboxDrainerLeaseDuration);
        var isSqlServer = auditCtx.Database.ProviderName == "Microsoft.EntityFrameworkCore.SqlServer";

        var claimed = isSqlServer
            ? await ClaimBatchSqlServerAsync(auditCtx, opts.OutboxDrainerBatchSize, now, leaseExpiry, ct)
            : await ClaimBatchPortableAsync(auditCtx, opts.OutboxDrainerBatchSize, now, leaseExpiry, ct);

        if (claimed.Count > 0)
            logger.LogDebug("Claimed {Count} outbox rows with lease until {LeaseExpiry:O}", claimed.Count, leaseExpiry);

        return claimed;
    }

    private async Task<List<AuditOutboxEntity>> ClaimBatchSqlServerAsync(
        AuditDbContext auditCtx, int batchSize, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct)
    {
        var entityType = auditCtx.Model.FindEntityType(typeof(AuditOutboxEntity))!;
        var schema = entityType.GetSchema() ?? "audit";
        var sql = $@"
            UPDATE TOP (@p0) [{schema}].[AuditOutbox]
            SET [Status] = @p1, [LeaseOwner] = @p2, [LeaseExpiresAt] = @p3
            OUTPUT INSERTED.Id, INSERTED.EnvelopeJson, INSERTED.EnvelopeVersion,
                   INSERTED.Status, INSERTED.CreatedAt, INSERTED.CompletedAt,
                   INSERTED.NextRetryAt, INSERTED.AttemptCount, INSERTED.LastError,
                   INSERTED.IdempotencyKey, INSERTED.LeaseOwner, INSERTED.LeaseExpiresAt
            WHERE [Status] = @p4
              AND ([NextRetryAt] IS NULL OR [NextRetryAt] <= @p5)
              AND ([LeaseExpiresAt] IS NULL OR [LeaseExpiresAt] < @p5)";

        var claimed = await auditCtx.AuditOutbox
            .FromSqlRaw(sql, batchSize, (int)AuditOutboxStatus.InFlight, _leaseOwnerId, leaseExpiry,
                (int)AuditOutboxStatus.Pending, now)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var row in claimed)
            auditCtx.Attach(row);

        return claimed;
    }

    private async Task<List<AuditOutboxEntity>> ClaimBatchPortableAsync(
        AuditDbContext auditCtx, int batchSize, DateTimeOffset now, DateTimeOffset leaseExpiry, CancellationToken ct)
    {
        var candidateIds = await auditCtx.AuditOutbox
            .Where(o => o.Status == AuditOutboxStatus.Pending && (o.NextRetryAt == null || o.NextRetryAt <= now))
            .OrderBy(static o => o.CreatedAt)
            .Take(batchSize)
            .Select(static o => o.Id)
            .ToListAsync(ct);

        if (candidateIds.Count == 0)
            return [];

        var updatedCount = await auditCtx.AuditOutbox
            .Where(o => candidateIds.Contains(o.Id) && o.Status == AuditOutboxStatus.Pending)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(o => o.Status, AuditOutboxStatus.InFlight)
                .SetProperty(o => o.LeaseOwner, _leaseOwnerId)
                .SetProperty(o => o.LeaseExpiresAt, leaseExpiry), ct);

        if (updatedCount == 0)
            return [];

        return await auditCtx.AuditOutbox
            .Where(o => o.LeaseOwner == _leaseOwnerId && o.Status == AuditOutboxStatus.InFlight)
            .ToListAsync(ct);
    }

    private async Task RecoverExpiredLeasesAsync(IServiceProvider sp, CancellationToken ct)
    {
        var auditCtx = sp.GetRequiredService<AuditDbContext>();
        var now = DateTimeOffset.UtcNow;

        var expiredRows = await auditCtx.AuditOutbox
            .Where(o => o.Status == AuditOutboxStatus.InFlight && o.LeaseExpiresAt != null && o.LeaseExpiresAt < now)
            .ToListAsync(ct);

        if (expiredRows.Count == 0)
            return;

        foreach (var row in expiredRows)
        {
            var previousOwner = row.LeaseOwner;
            var previousExpiry = row.LeaseExpiresAt;

            row.Status = AuditOutboxStatus.Pending;
            row.LeaseOwner = null;
            row.LeaseExpiresAt = null;

            logger.LogWarning(
                "Recovered outbox row {RowId} from expired lease (owner={LeaseOwner}, expired={LeaseExpiry:O}). AttemptCount unchanged at {AttemptCount}.",
                row.Id, previousOwner, previousExpiry, row.AttemptCount);
        }

        await auditCtx.SaveChangesAsync(ct);
        AuditMetrics.LeasesRecovered.Add(expiredRows.Count);
        logger.LogInformation("Recovered {Count} outbox rows from expired leases", expiredRows.Count);
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
