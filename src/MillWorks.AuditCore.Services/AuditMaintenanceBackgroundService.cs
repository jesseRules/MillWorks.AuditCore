using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.Maintenance;

/// <summary>
/// Background service for audit maintenance tasks
/// </summary>
public sealed class AuditMaintenanceBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<AuditMaintenanceBackgroundService> logger,
    IConfiguration configuration)
    : BackgroundService
{
    /// <summary>
    /// Execute the background service
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        int interval = configuration.GetValue("Audit:MaintenanceIntervalHours", 24);

        // Wait for application to fully start
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting scheduled audit maintenance");

                using IServiceScope scope = serviceProvider.CreateScope();

                // Run maintenance tasks
                IAuditMaintenanceService maintenanceService =
                    scope.ServiceProvider.GetRequiredService<IAuditMaintenanceService>();
                IAuditComplianceService complianceService =
                    scope.ServiceProvider.GetRequiredService<IAuditComplianceService>();
                IAuditArchivalService archivalService =
                    scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();

                ITamperDetectionService tamperService =
                    scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();

                // 1. Archive old events first
                if (configuration.GetValue("Audit:Archive:Enabled", true))
                {
                    int archiveAfterDays = configuration.GetValue("Audit:Archive:ArchiveAfterDays", 90);
                    DateTimeOffset archiveBefore = DateTimeOffset.UtcNow.AddDays(-archiveAfterDays);

                    AuditArchivalResult archiveResult =
                        await archivalService.ArchiveAuditEventsAsync(archiveBefore, null, stoppingToken);
                    if (archiveResult.Success)
                    {
                        logger.LogInformation("Archived {Count} audit events", archiveResult.EventCount);
                    }
                }

                // 2. Clean up old events (that have been archived)
                int retentionDays = configuration.GetValue("Audit:RetentionDays", 365);
                int deletedCount = await maintenanceService.CleanupOldAuditEventsAsync(retentionDays, stoppingToken);
                logger.LogInformation("Cleaned up {Count} old audit events", deletedCount);

                // 3. Apply retention policies
                await complianceService.ApplyRetentionPolicyAsync(stoppingToken);

                // 4. Check for tampering (includes sequence integrity verification)
                List<TamperAlert> tamperAlerts = await tamperService.DetectTamperingAsync(24, stoppingToken);
                if (tamperAlerts.Count > 0)
                {
                    logger.LogCritical("TAMPERING DETECTED: {Count} alerts found!", tamperAlerts.Count);
                    foreach (TamperAlert alert in tamperAlerts)
                    {
                        logger.LogCritical("Tamper Alert: {Type} - {Description} (Severity: {Severity})",
                            alert.AlertType, alert.Description, alert.Severity);
                    }
                }

                // 6. Optimize tables
                if (configuration.GetValue("Audit:OptimizationEnabled", true))
                {
                    await maintenanceService.OptimizeAuditTablesAsync(stoppingToken);
                }

                // 7. Log statistics
                Dictionary<string, object?> stats = await maintenanceService.GetAuditStatisticsAsync(stoppingToken);
                logger.LogInformation("Audit Statistics: Total Events: {TotalEvents}, Database Size: {DbSize}MB",
                    stats["TotalEvents"],
                    stats.TryGetValue("DatabaseSizeKB", out object? stat) ? (long)(stat ?? 0) / 1024 : 0);

                logger.LogInformation("Audit maintenance completed successfully");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during audit maintenance");
            }

            // Wait for next interval
            await Task.Delay(TimeSpan.FromHours(interval), stoppingToken);
        }
    }
}