using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Background service for scheduled archive creation based on retention policy.
/// </summary>
public sealed class ArchiveCreationBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<ArchiveCreationBackgroundService> logger,
    ArchivalOptions archivalOptions)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan archivalInterval = TimeSpan.FromHours(archivalOptions.ArchivalIntervalHours);

        // Wait for application to fully start
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting scheduled archive creation");

                using IServiceScope scope = serviceProvider.CreateScope();
                IAuditArchivalService archivalService =
                    scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();

                DateTimeOffset archiveBefore = DateTimeOffset.UtcNow.AddDays(-archivalOptions.RetentionDays);

                AuditArchivalResult result =
                    await archivalService.ArchiveAuditEventsAsync(archiveBefore, null, stoppingToken);

                if (result.Success)
                {
                    logger.LogInformation(
                        "Scheduled archival completed: {EventCount} events archived",
                        result.EventCount);
                }
                else
                {
                    logger.LogWarning("Scheduled archival completed with issues: {Message}", result.Message);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during scheduled archive creation cycle");
            }

            await Task.Delay(archivalInterval, stoppingToken);
        }
    }
}
