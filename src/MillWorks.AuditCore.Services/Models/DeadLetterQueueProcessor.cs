using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Background service to periodically reprocess failed events
/// </summary>
public sealed class DeadLetterQueueProcessor : BackgroundService
{
    private static readonly TimeSpan DefaultInterEventDelay = TimeSpan.FromSeconds(1);
    private const string ReprocessLockName = "audit:dead-letter-queue:reprocess";

    /// <summary>
    /// Service provider for creating scopes
    /// </summary>
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Logger
    /// </summary>
    private readonly ILogger<DeadLetterQueueProcessor> _logger;

    /// <summary>
    /// Resilience options
    /// </summary>
    private readonly IOptions<ResilienceOptions> _options;

    private readonly TimeSpan? _intervalOverride;
    private readonly TimeSpan _interEventDelay;

    /// <summary>
    /// Constructor for the dead letter queue processor
    /// </summary>
    public DeadLetterQueueProcessor(
        IServiceProvider serviceProvider,
        ILogger<DeadLetterQueueProcessor> logger,
        IOptions<ResilienceOptions> options)
        : this(serviceProvider, logger, options, null, null)
    {
    }

    internal DeadLetterQueueProcessor(
        IServiceProvider serviceProvider,
        ILogger<DeadLetterQueueProcessor> logger,
        IOptions<ResilienceOptions> options,
        TimeSpan? intervalOverride,
        TimeSpan? interEventDelay)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options;
        _intervalOverride = intervalOverride;
        _interEventDelay = interEventDelay ?? DefaultInterEventDelay;
    }

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        ValidateRequiredServices();
        return base.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Execute the dead letter queue processor
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = _options.Value;

        if (!options.EnableDeadLetterQueue || !options.EnableBackgroundProcessor)
        {
            _logger.LogInformation("DeadLetterQueueProcessor disabled by configuration");
            return;
        }

        var interval = _intervalOverride ?? TimeSpan.FromMinutes(options.ReprocessIntervalMinutes);

        _logger.LogInformation("Dead letter queue processor started. Interval: {Interval}", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval, checking for cancellation
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping the service
                break;
            }

            try
            {
                await ProcessOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing dead letter queue");
            }
        }
    }

    internal async Task ProcessOnceAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        using var scope = _serviceProvider.CreateScope();

        var dlq = scope.ServiceProvider.GetRequiredService<IAuditDeadLetterQueue>();
        var lockService = scope.ServiceProvider.GetRequiredService<IAuditDistributedLockService>();

        var maxRetries = options.MaxRetries;
        var maxCount = options.DeadLetterQueueMaxBatchSize;
        var lockExpiry = TimeSpan.FromMinutes(Math.Max(options.ReprocessIntervalMinutes, 1.0));

        using var reprocessLock = await lockService.AcquireLockAsync(ReprocessLockName, lockExpiry, cancellationToken);

        var events = await dlq.GetFailedEventsAsync(maxCount);
        var eventsToRetry = events
            .Where(e => !e.IsProcessed && e.RetryCount < maxRetries)
            .ToList();

        var successCount = 0;
        var failureCount = 0;

        if (eventsToRetry.Count > 0)
        {
            _logger.LogInformation("Attempting to reprocess {Count} dead letter events", eventsToRetry.Count);

            foreach (var evt in eventsToRetry)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var reprocessed = await dlq.ReprocessEventAsync(evt.Id);
                    if (reprocessed)
                    {
                        successCount++;
                    }
                    else
                    {
                        failureCount++;
                        _logger.LogWarning("Dead letter event {Id} was not reprocessed and remains in the queue", evt.Id);
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogError(ex, "Failed to reprocess dead letter event {Id}", evt.Id);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await Task.Delay(_interEventDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        var purgedCount = await dlq.PurgeProcessedEventsAsync();
        if (purgedCount > 0)
            _logger.LogInformation("Purged {Count} processed events from dead letter queue", purgedCount);

        var stats = await dlq.GetStatisticsAsync();
        _logger.LogInformation(
            "Dead letter queue reprocessing completed. Success={Success}, Failure={Failure}, Total={Total}, Pending={Pending}, Failed={Failed}",
            successCount,
            failureCount,
            stats.TotalEvents,
            stats.PendingEvents,
            stats.FailedEvents);
    }

    private void ValidateRequiredServices()
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            _ = scope.ServiceProvider.GetRequiredService<IAuditDeadLetterQueue>();
            _ = scope.ServiceProvider.GetRequiredService<IAuditDistributedLockService>();
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "DeadLetterQueueProcessor requires IAuditDeadLetterQueue and IAuditDistributedLockService to be registered.",
                ex);
        }
    }
}
