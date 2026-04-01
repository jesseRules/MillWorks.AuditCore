using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.AspNetCore.Extensions;

/// <summary>
/// Health check that reports the state of the integrity work item pipeline.
/// Returns <see cref="HealthStatus.Healthy"/> when no work items are stale or failed,
/// <see cref="HealthStatus.Degraded"/> when pending work items exceed thresholds,
/// and <see cref="HealthStatus.Unhealthy"/> when permanently failed work items exist.
/// </summary>
public sealed class IntegrityHealthCheck(
    IServiceScopeFactory scopeFactory,
    IAuditDiagnostics? diagnostics = null) : IHealthCheck
{
    /// <summary>
    /// Pending work items older than this are considered stale
    /// </summary>
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(10);

    /// <summary>
    /// More than this many stale pending items triggers Degraded
    /// </summary>
    private const int DegradedPendingThreshold = 10;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();

            var staleCutoff = DateTimeOffset.UtcNow - StaleThreshold;

            var failedCount = await dbContext.IntegrityWorkItems
                .CountAsync(w => w.Status == IntegrityStatus.Failed, cancellationToken);

            var stalePendingCount = await dbContext.IntegrityWorkItems
                .CountAsync(w => w.Status == IntegrityStatus.Pending && w.CreatedAt < staleCutoff, cancellationToken);

            var totalPendingCount = await dbContext.IntegrityWorkItems
                .CountAsync(w => w.Status == IntegrityStatus.Pending, cancellationToken);

            var data = new Dictionary<string, object>
            {
                ["pending_total"] = totalPendingCount,
                ["pending_stale"] = stalePendingCount,
                ["failed"] = failedCount
            };

            if (diagnostics is not null)
            {
                data["batch_flush_count"] = diagnostics.IntegrityBatchFlushCount;
                data["batch_flush_failures"] = diagnostics.IntegrityBatchFlushFailureCount;
                data["reconciliation_successes"] = diagnostics.IntegrityReconciliationSuccessCount;
                data["reconciliation_failures"] = diagnostics.IntegrityReconciliationFailureCount;
                data["permanent_failures"] = diagnostics.IntegrityPermanentFailureCount;
            }

            if (failedCount > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"{failedCount} integrity work item(s) permanently failed — operator intervention required.",
                    data: data);
            }

            if (stalePendingCount > DegradedPendingThreshold)
            {
                return HealthCheckResult.Degraded(
                    $"{stalePendingCount} integrity work item(s) have been pending for more than {StaleThreshold.TotalMinutes} minutes.",
                    data: data);
            }

            return HealthCheckResult.Healthy(
                $"Integrity pipeline healthy. {totalPendingCount} pending, 0 failed.",
                data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(
                "Failed to query integrity work item status.",
                exception: ex);
        }
    }
}
