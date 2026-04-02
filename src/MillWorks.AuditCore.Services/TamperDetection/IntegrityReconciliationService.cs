using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.TamperDetection;

/// <summary>
/// Background service that reconciles pending integrity work items on startup and on schedule.
/// Picks up work items that were left in <see cref="IntegrityStatus.Pending"/> due to hard kill,
/// crash, or batcher failure, and retries integrity record creation. Marks permanently failed
/// work items and emits security events for operator visibility.
/// </summary>
public sealed class IntegrityReconciliationService(
    IServiceScopeFactory scopeFactory,
    ILogger<IntegrityReconciliationService> logger,
    IAuditDiagnostics? diagnostics = null)
    : BackgroundService
{
    /// <summary>
    /// Maximum number of attempts before marking a work item as Failed
    /// </summary>
    private const int MaxAttempts = 5;

    /// <summary>
    /// How long a work item must be Pending before reconciliation considers it stale
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Batch size for reconciliation processing
    /// </summary>
    private const int ReconciliationBatchSize = 100;

    /// <summary>
    /// Duration of a reconciliation lease. Prevents multiple instances from processing
    /// the same pending work item at the same time.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Interval between scheduled reconciliation runs (after the initial startup run)
    /// </summary>
    private static readonly TimeSpan ReconciliationInterval = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Stable identifier for this reconciler instance when claiming work items.
    /// </summary>
    private readonly string _instanceId = $"reconciler-{Guid.NewGuid():N}";

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait briefly for application startup to complete
        await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);

        logger.LogInformation("IntegrityReconciliationService: running startup reconciliation");
        await ReconcileAsync(stoppingToken);

        // Schedule periodic reconciliation
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReconciliationInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await ReconcileAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Performs a single reconciliation pass: finds stale Pending work items,
    /// retries integrity record creation, and marks permanently failed items.
    /// </summary>
    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();
            var tamperDetection = scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();

            var staleWorkItems = await ClaimStaleWorkItemsAsync(dbContext, cancellationToken);

            if (staleWorkItems.Count == 0)
                return;

            logger.LogInformation(
                "IntegrityReconciliationService: found {Count} stale pending work items",
                staleWorkItems.Count);

            int succeeded = 0;
            int failed = 0;
            int markedFailed = 0;

            foreach (var workItem in staleWorkItems)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Check if this work item has exceeded max attempts
                    if (workItem.AttemptCount >= MaxAttempts)
                    {
                        workItem.Status = IntegrityStatus.Failed;
                        workItem.LastAttemptAt = DateTimeOffset.UtcNow;
                        workItem.LeaseOwner = null;
                        workItem.LeaseExpiresAt = null;

                        // Also mark the audit event as Failed
                        var auditEvent = await dbContext.AuditEvents
                            .FirstOrDefaultAsync(e => e.EventId == workItem.EventId, cancellationToken);
                        if (auditEvent is not null)
                            auditEvent.IntegrityStatus = IntegrityStatus.Failed;

                        // Record a security event for operator visibility
                        dbContext.SecurityEvents.Add(new AuditSecurityEventEntity
                        {
                            EventType = SecurityEventType.IntegrityViolation,
                            Message = $"Integrity record creation permanently failed for event {workItem.EventId} after {workItem.AttemptCount} attempts.",
                            DetailsJson = workItem.LastError,
                            Severity = SecurityEventSeverity.High,
                            Status = SecurityEventStatus.Open,
                            DetectedAt = DateTimeOffset.UtcNow,
                            DetectedBy = nameof(IntegrityReconciliationService),
                            RelatedAuditEventId = workItem.EventId
                        });

                        await dbContext.SaveChangesAsync(cancellationToken);
                        markedFailed++;
                        diagnostics?.Increment(AuditDiagnosticCounter.IntegrityPermanentFailure);
                        continue;
                    }

                    // Load the audit event to build the integrity DTO
                    var eventEntity = await dbContext.AuditEvents
                        .AsNoTracking()
                        .FirstOrDefaultAsync(e => e.EventId == workItem.EventId, cancellationToken);

                    if (eventEntity is null)
                    {
                        // The audit event was deleted (e.g., by archival or maintenance).
                        // Remove the orphaned work item.
                        workItem.Status = IntegrityStatus.Failed;
                        workItem.LastError = "Audit event no longer exists";
                        workItem.LastAttemptAt = DateTimeOffset.UtcNow;
                        workItem.LeaseOwner = null;
                        workItem.LeaseExpiresAt = null;
                        await dbContext.SaveChangesAsync(cancellationToken);
                        markedFailed++;
                        continue;
                    }

                    // Check if an integrity record already exists (maybe the batcher succeeded
                    // but died before marking the work item complete)
                    var integrityExists = await dbContext.AuditIntegrity
                        .AnyAsync(i => i.EventId == workItem.EventId, cancellationToken);

                    if (integrityExists)
                    {
                        // Integrity record exists — just mark the work item and event as Reconciled
                        workItem.Status = IntegrityStatus.Reconciled;
                        workItem.CompletedAt = DateTimeOffset.UtcNow;
                        workItem.LeaseOwner = null;
                        workItem.LeaseExpiresAt = null;

                        await dbContext.AuditEvents
                            .Where(e => e.EventId == workItem.EventId && e.IntegrityStatus == IntegrityStatus.Pending)
                            .ExecuteUpdateAsync(s => s
                                .SetProperty(static e => e.IntegrityStatus, IntegrityStatus.Reconciled), cancellationToken);

                        await dbContext.SaveChangesAsync(cancellationToken);
                        succeeded++;
                    diagnostics?.Increment(AuditDiagnosticCounter.IntegrityReconciliationSuccess);
                        continue;
                    }

                    // Create the integrity record
                    var integrityDto = new Abstractions.Dto.AuditIntegrityDto
                    {
                        EventId = eventEntity.EventId,
                        InsertedDate = eventEntity.InsertedDate,
                        LastUpdatedDate = eventEntity.LastUpdatedDate,
                        JsonData = eventEntity.JsonData,
                        EventType = eventEntity.EventType,
                        User = eventEntity.User,
                        UserId = eventEntity.UserId
                    };

                    await tamperDetection.CreateIntegrityRecordAsync(integrityDto, cancellationToken);

                    // Mark as Reconciled
                    workItem.Status = IntegrityStatus.Reconciled;
                    workItem.CompletedAt = DateTimeOffset.UtcNow;
                    workItem.LeaseOwner = null;
                    workItem.LeaseExpiresAt = null;

                    await dbContext.AuditEvents
                        .Where(e => e.EventId == workItem.EventId && e.IntegrityStatus == IntegrityStatus.Pending)
                        .ExecuteUpdateAsync(s => s
                            .SetProperty(static e => e.IntegrityStatus, IntegrityStatus.Reconciled), cancellationToken);

                    await dbContext.SaveChangesAsync(cancellationToken);
                    succeeded++;
                    diagnostics?.Increment(AuditDiagnosticCounter.IntegrityReconciliationSuccess);
                }
                catch (Exception ex)
                {
                    failed++;
                    diagnostics?.Increment(AuditDiagnosticCounter.IntegrityReconciliationFailure);
                    workItem.AttemptCount++;
                    workItem.LastAttemptAt = DateTimeOffset.UtcNow;
                    workItem.LastError = ex.Message.Length > 2000
                        ? ex.Message[..2000]
                        : ex.Message;
                    workItem.LeaseOwner = null;
                    workItem.LeaseExpiresAt = null;

                    try
                    {
                        await dbContext.SaveChangesAsync(cancellationToken);
                    }
                    catch (Exception saveEx)
                    {
                        logger.LogWarning(saveEx,
                            "IntegrityReconciliationService: failed to save attempt metadata for work item {WorkItemId}",
                            workItem.Id);
                    }

                    logger.LogWarning(ex,
                        "IntegrityReconciliationService: failed to reconcile work item {WorkItemId} for event {EventId} (attempt {Attempt}/{Max})",
                        workItem.Id, workItem.EventId, workItem.AttemptCount, MaxAttempts);
                }
            }

            logger.LogInformation(
                "IntegrityReconciliationService: reconciliation complete — {Succeeded} succeeded, {Failed} retryable failures, {MarkedFailed} permanently failed",
                succeeded, failed, markedFailed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "IntegrityReconciliationService: reconciliation pass failed");
        }
    }

    /// <summary>
    /// Claims a batch of stale pending work items using per-row leases so only one
    /// reconciler instance processes a given item at a time.
    /// </summary>
    private async Task<List<AuditIntegrityWorkItemEntity>> ClaimStaleWorkItemsAsync(
        AuditApplicationDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var staleCutoff = now - StaleThreshold;
        var leaseExpiresAt = now.Add(LeaseDuration);

        var candidateIds = await dbContext.IntegrityWorkItems
            .Where(w => w.Status == IntegrityStatus.Pending
                        && w.CreatedAt < staleCutoff
                        && (w.LeaseExpiresAt == null || w.LeaseExpiresAt < now))
            .OrderBy(static w => w.CreatedAt)
            .Select(static w => w.Id)
            .Take(ReconciliationBatchSize)
            .ToListAsync(cancellationToken);

        var claimedItems = new List<AuditIntegrityWorkItemEntity>(candidateIds.Count);

        foreach (var candidateId in candidateIds)
        {
            var claimed = await dbContext.IntegrityWorkItems
                .Where(w => w.Id == candidateId
                            && w.Status == IntegrityStatus.Pending
                            && (w.LeaseExpiresAt == null || w.LeaseExpiresAt < now))
                .ExecuteUpdateAsync(s => s
                        .SetProperty(static w => w.LeaseOwner, _instanceId)
                        .SetProperty(static w => w.LeaseExpiresAt, leaseExpiresAt),
                    cancellationToken);

            if (claimed != 1)
                continue;

            var workItem = await dbContext.IntegrityWorkItems
                .FirstAsync(w => w.Id == candidateId, cancellationToken);
            claimedItems.Add(workItem);
        }

        return claimedItems;
    }
}
