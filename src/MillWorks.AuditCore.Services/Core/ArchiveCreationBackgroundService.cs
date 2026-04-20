using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
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
    IOptions<ArchivalOptions> archivalOptions)
    : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = archivalOptions.Value;

        if (!options.EnableBackgroundArchival)
        {
            logger.LogInformation("ArchiveCreationBackgroundService disabled by configuration");
            return;
        }

        TimeSpan archivalInterval = TimeSpan.FromHours(options.ArchivalIntervalHours);

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

                DateTimeOffset archiveBefore = DateTimeOffset.UtcNow.AddDays(-options.RetentionDays);

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
