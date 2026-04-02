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
public sealed class ArchiveVerificationBackgroundService : BackgroundService
{
    private static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(5);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ArchiveVerificationBackgroundService> _logger;
    private readonly ArchivalOptions _archivalOptions;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan? _intervalOverride;

    public ArchiveVerificationBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ArchiveVerificationBackgroundService> logger,
        ArchivalOptions archivalOptions)
        : this(serviceProvider, logger, archivalOptions, TimeProvider.System, null, null)
    {
    }

    internal ArchiveVerificationBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<ArchiveVerificationBackgroundService> logger,
        ArchivalOptions archivalOptions,
        TimeProvider timeProvider,
        TimeSpan? startupDelay,
        TimeSpan? intervalOverride)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _archivalOptions = archivalOptions ?? throw new ArgumentNullException(nameof(archivalOptions));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startupDelay = startupDelay ?? DefaultStartupDelay;
        _intervalOverride = intervalOverride;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateRequiredServices();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Execute the background service
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var verificationInterval = _intervalOverride ?? TimeSpan.FromHours(_archivalOptions.VerificationIntervalHours);

        if (_startupDelay > TimeSpan.Zero)
            await Task.Delay(_startupDelay, stoppingToken);

        var cycle = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            cycle++;
            var cycleStartedAt = _timeProvider.GetUtcNow();

            try
            {
                _logger.LogInformation("Starting scheduled archive verification cycle {Cycle}", cycle);
                var (verifiedCount, failedCount) = await ExecuteCycleAsync(stoppingToken);

                _logger.LogInformation(
                    "Archive verification cycle {Cycle} completed in {DurationMs} ms with {Verified} verified and {Failed} failed. Next scheduled run: {NextRunUtc}",
                    cycle,
                    (_timeProvider.GetUtcNow() - cycleStartedAt).TotalMilliseconds,
                    verifiedCount,
                    failedCount,
                    _timeProvider.GetUtcNow().Add(verificationInterval));

                if (failedCount > 0)
                    _logger.LogWarning("{Count} archives failed verification - investigate immediately!", failedCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Error during archive verification cycle {Cycle} after {DurationMs} ms. Next scheduled run: {NextRunUtc}",
                    cycle,
                    (_timeProvider.GetUtcNow() - cycleStartedAt).TotalMilliseconds,
                    _timeProvider.GetUtcNow().Add(verificationInterval));
            }

            try
            {
                await Task.Delay(verificationInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task<(int VerifiedCount, int FailedCount)> ExecuteCycleAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();
        var archiveService = scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();
        var archiveRepository = scope.ServiceProvider.GetRequiredService<IArchiveRecordRepository>();

        var archivesToVerify = await archiveRepository.GetArchivesNeedingVerificationAsync(
            _archivalOptions.VerificationIntervalHours,
            stoppingToken);

        var verifiedCount = 0;
        var failedCount = 0;

        foreach (var archive in archivesToVerify)
        {
            try
            {
                var isValid = await archiveService.ValidateArchiveIntegrityAsync(archive.ArchiveId, stoppingToken);
                if (isValid)
                {
                    verifiedCount++;
                    _logger.LogDebug("Archive {ArchiveId} verification passed", archive.ArchiveId);
                }
                else
                {
                    failedCount++;
                    _logger.LogError("Archive {ArchiveId} verification failed", archive.ArchiveId);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex, "Error verifying archive {ArchiveId}", archive.ArchiveId);
            }
        }

        return (verifiedCount, failedCount);
    }

    private void ValidateRequiredServices()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();
            _ = scope.ServiceProvider.GetRequiredService<IArchiveRecordRepository>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "ArchiveVerificationBackgroundService requires IAuditArchivalService and IArchiveRecordRepository to be registered.",
                ex);
        }
    }
}
