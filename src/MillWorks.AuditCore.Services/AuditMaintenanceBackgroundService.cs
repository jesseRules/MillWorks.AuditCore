using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.Maintenance;

/// <summary>
/// Background service for audit maintenance tasks
/// </summary>
public sealed class AuditMaintenanceBackgroundService : BackgroundService
{
    private static readonly TimeSpan _defaultStartupDelay = TimeSpan.FromMinutes(1);

    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<AuditMaintenanceBackgroundService> _logger;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan? _intervalOverride;

    public AuditMaintenanceBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AuditMaintenanceBackgroundService> logger,
        IConfiguration configuration)
        : this(serviceProvider, logger, configuration, TimeProvider.System, null, null)
    {
    }

    /// <summary>
    /// Constructor with additional parameters for testing and configuration overrides
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="logger"></param>
    /// <param name="configuration"></param>
    /// <param name="timeProvider"></param>
    /// <param name="startupDelay"></param>
    /// <param name="intervalOverride"></param>
    /// <exception cref="ArgumentNullException"></exception>
    internal AuditMaintenanceBackgroundService(
        IServiceProvider serviceProvider,
        ILogger<AuditMaintenanceBackgroundService> logger,
        IConfiguration configuration,
        TimeProvider timeProvider,
        TimeSpan? startupDelay,
        TimeSpan? intervalOverride)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _startupDelay = startupDelay ?? _defaultStartupDelay;
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
        var interval = _intervalOverride ?? TimeSpan.FromHours(_configuration.GetValue("Audit:MaintenanceIntervalHours", 24));

        if (_startupDelay > TimeSpan.Zero)
            await Task.Delay(_startupDelay, stoppingToken);

        var cycle = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            cycle++;
            var cycleStartedAt = _timeProvider.GetUtcNow();

            try
            {
                _logger.LogInformation("Starting scheduled audit maintenance cycle {Cycle}", cycle);
                await ExecuteCycleAsync(stoppingToken);

                var duration = _timeProvider.GetUtcNow() - cycleStartedAt;
                _logger.LogInformation(
                    "Audit maintenance cycle {Cycle} completed in {DurationMs} ms. Next scheduled run: {NextRunUtc}",
                    cycle,
                    duration.TotalMilliseconds,
                    _timeProvider.GetUtcNow().Add(interval));
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var duration = _timeProvider.GetUtcNow() - cycleStartedAt;
                _logger.LogError(
                    ex,
                    "Error during audit maintenance cycle {Cycle} after {DurationMs} ms. Next scheduled run: {NextRunUtc}",
                    cycle,
                    duration.TotalMilliseconds,
                    _timeProvider.GetUtcNow().Add(interval));
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Executes a single cycle of audit maintenance tasks, including cleanup, archival, compliance checks, and tamper detection.
    /// </summary>
    /// <param name="stoppingToken"></param>
    private async Task ExecuteCycleAsync(CancellationToken stoppingToken)
    {
        using IServiceScope scope = _serviceProvider.CreateScope();

        var maintenanceService = scope.ServiceProvider.GetRequiredService<IAuditMaintenanceService>();
        var complianceService = scope.ServiceProvider.GetRequiredService<IAuditComplianceService>();
        var archivalService = scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();
        var tamperService = scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();

        if (_configuration.GetValue("Audit:Archive:Enabled", true))
        {
            var archiveAfterDays = _configuration.GetValue("Audit:Archive:ArchiveAfterDays", 90);
            var archiveBefore = _timeProvider.GetUtcNow().AddDays(-archiveAfterDays);

            var archiveResult = await archivalService.ArchiveAuditEventsAsync(archiveBefore, null, stoppingToken);
            if (archiveResult.Success)
                _logger.LogInformation("Archived {Count} audit events", archiveResult.EventCount);
        }

        var retentionDays = _configuration.GetValue("Audit:RetentionDays", 365);
        var deletedCount = await maintenanceService.CleanupOldAuditEventsAsync(retentionDays, stoppingToken);
        _logger.LogInformation("Cleaned up {Count} old audit events", deletedCount);

        await complianceService.ApplyRetentionPolicyAsync(stoppingToken);

        var tamperAlerts = await tamperService.DetectTamperingAsync(24, stoppingToken);
        if (tamperAlerts.Count > 0)
        {
            _logger.LogCritical("TAMPERING DETECTED: {Count} alerts found!", tamperAlerts.Count);
            foreach (var alert in tamperAlerts)
            {
                _logger.LogCritical(
                    "Tamper Alert: {Type} - {Description} (Severity: {Severity})",
                    alert.AlertType,
                    alert.Description,
                    alert.Severity);
            }
        }

        if (_configuration.GetValue("Audit:OptimizationEnabled", true))
            await maintenanceService.OptimizeAuditTablesAsync(stoppingToken);

        var stats = await maintenanceService.GetAuditStatisticsAsync(stoppingToken);
        _logger.LogInformation(
            "Audit Statistics: Total Events: {TotalEvents}, Database Size: {DbSize}MB",
            stats["TotalEvents"],
            stats.TryGetValue("DatabaseSizeKB", out var stat) ? (long)(stat ?? 0) / 1024 : 0);
    }

    /// <summary>
    /// Validates that all required services are registered in the DI container. This method is called at startup to fail fast if any dependencies are missing.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    private void ValidateRequiredServices()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IAuditMaintenanceService>();
            _ = scope.ServiceProvider.GetRequiredService<IAuditComplianceService>();
            _ = scope.ServiceProvider.GetRequiredService<IAuditArchivalService>();
            _ = scope.ServiceProvider.GetRequiredService<ITamperDetectionService>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "AuditMaintenanceBackgroundService requires IAuditMaintenanceService, IAuditComplianceService, IAuditArchivalService, and ITamperDetectionService to be registered.",
                ex);
        }
    }
}
