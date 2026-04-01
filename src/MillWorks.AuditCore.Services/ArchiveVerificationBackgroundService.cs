using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Background service for periodic archive verification
/// </summary>
public sealed class ArchiveVerificationBackgroundService(
    IServiceProvider serviceProvider,
    ILogger<ArchiveVerificationBackgroundService> logger,
    ArchivalOptions archivalOptions)
    : BackgroundService
{
    /// <summary>
    /// Execute the background service
    /// </summary>
    /// <param name="stoppingToken"></param>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan verificationInterval = TimeSpan.FromHours(archivalOptions.VerificationIntervalHours);

        // Wait for application to fully start
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("Starting scheduled archive verification");

                using IServiceScope scope = serviceProvider.CreateScope();
                IAuditArchivalService archiveService =
                    scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();
                IArchiveRecordRepository archiveRepository =
                    scope.ServiceProvider.GetRequiredService<IArchiveRecordRepository>();

                // Get archives that need verification
                IEnumerable<AuditArchiveRecordEntity> archivesToVerify =
                    await archiveRepository.GetArchivesNeedingVerificationAsync(
                        archivalOptions.VerificationIntervalHours, stoppingToken);

                int verifiedCount = 0;
                int failedCount = 0;

                foreach (AuditArchiveRecordEntity archive in archivesToVerify)
                {
                    try
                    {
                        bool isValid = await archiveService.ValidateArchiveIntegrityAsync(
                            archive.ArchiveId, stoppingToken);

                        if (isValid)
                        {
                            verifiedCount++;
                            logger.LogDebug("Archive {ArchiveId} verification passed", archive.ArchiveId);
                        }
                        else
                        {
                            failedCount++;
                            logger.LogError("Archive {ArchiveId} verification failed", archive.ArchiveId);
                        }
                    }
                    catch (Exception ex)
                    {
                        failedCount++;
                        logger.LogError(ex, "Error verifying archive {ArchiveId}", archive.ArchiveId);
                    }
                }

                logger.LogInformation("Archive verification completed: {Verified} verified, {Failed} failed",
                    verifiedCount, failedCount);

                if (failedCount > 0)
                {
                    logger.LogWarning("{Count} archives failed verification - investigate immediately!", failedCount);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during archive verification cycle");
            }

            // Wait for next verification cycle
            await Task.Delay(verificationInterval, stoppingToken);
        }
    }
}