using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Models;

/// <summary>
/// Background service to periodically reprocess failed events
/// </summary>
public sealed class DeadLetterQueueProcessor : BackgroundService
{
    /// <summary>
    /// Service provider for creating scopes
    /// </summary>
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Logger
    /// </summary>
    private readonly ILogger<DeadLetterQueueProcessor> _logger;

    /// <summary>
    /// Configuration
    /// </summary>
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Constructor for the dead letter queue processor
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="logger"></param>
    /// <param name="configuration"></param>
    public DeadLetterQueueProcessor(
        IServiceProvider serviceProvider,
        ILogger<DeadLetterQueueProcessor> logger,
        IConfiguration configuration)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
        _configuration = configuration;
    }

    /// <summary>
    /// Execute the dead letter queue processor
    /// </summary>
    /// <param name="stoppingToken"></param>
    /// <returns></returns>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool enabled = _configuration.GetValue("Audit:DeadLetterQueue:AutoReprocess", true);
        if (!enabled)
        {
            _logger.LogInformation("Dead letter queue auto-reprocessing is disabled");
            return;
        }

        double intervalMinutes = _configuration.GetValue("Audit:DeadLetterQueue:ReprocessIntervalMinutes", 60.0);
        int maxRetries = _configuration.GetValue("Audit:DeadLetterQueue:MaxRetries", 3);

        _logger.LogInformation("Dead letter queue processor started. Interval: {Interval} minutes", intervalMinutes);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait for the interval, checking for cancellation
                await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping the service
                break;
            }

            try
            {
                // Get the scope factory from service provider
                IServiceScopeFactory scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
                using IServiceScope scope = scopeFactory.CreateScope();
                
                IAuditDeadLetterQueue dlq = scope.ServiceProvider.GetRequiredService<IAuditDeadLetterQueue>();

                // Get unprocessed events with retry count less than max
                List<DeadLetterAuditEvent> events = await dlq.GetFailedEventsAsync();
                List<DeadLetterAuditEvent> eventsToRetry = events
                    .Where(e => !e.IsProcessed && e.RetryCount < maxRetries)
                    .ToList();

                if (eventsToRetry.Any())
                {
                    _logger.LogInformation("Attempting to reprocess {Count} dead letter events", eventsToRetry.Count);

                    foreach (DeadLetterAuditEvent evt in eventsToRetry)
                    {
                        if (stoppingToken.IsCancellationRequested)
                            break;

                        try
                        {
                            await dlq.ReprocessEventAsync(evt.Id);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Failed to reprocess dead letter event {Id}", evt.Id);
                        }

                        // Small delay between retries (but don't break the loop if cancelled)
                        if (stoppingToken.IsCancellationRequested) continue;
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // Delay was cancelled, but we've already processed this event
                            // Continue to check cancellation at the top of the next iteration
                        }
                    }
                }

                // Purge successfully processed events
                int purgedCount = await dlq.PurgeProcessedEventsAsync();
                if (purgedCount > 0)
                {
                    _logger.LogInformation("Purged {Count} processed events from dead letter queue", purgedCount);
                }

                // Log statistics
                DeadLetterStatistics stats = await dlq.GetStatisticsAsync();
                _logger.LogInformation("Dead letter queue stats: Total={Total}, Pending={Pending}, Failed={Failed}",
                    stats.TotalEvents, stats.PendingEvents, stats.FailedEvents);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing dead letter queue");
            }
        }
    }
}