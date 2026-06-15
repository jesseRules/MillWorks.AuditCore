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
/// Injects <see cref="TimeProvider"/> for deterministic testing and configures
/// startup delay via <see cref="ArchivalOptions.StartupDelaySeconds"/>.
/// </summary>
public sealed class ArchiveCreationBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<ArchiveCreationBackgroundService> logger,
    IOptions<ArchivalOptions> archivalOptions,
    TimeProvider? timeProvider = null)
    : BackgroundService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
        TimeSpan startupDelay = TimeSpan.FromSeconds(options.StartupDelaySeconds);

        await Task.Delay(startupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting scheduled archive creation");

                using IServiceScope scope = serviceProvider.CreateScope();
                IAuditArchivalService archivalService =
                    scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();

                DateTimeOffset archiveBefore = _timeProvider.GetUtcNow().AddDays(-options.RetentionDays);

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
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Error during scheduled archive creation cycle");
            }

            await Task.Delay(archivalInterval, stoppingToken);
        }
    }
}
