using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;

/// <summary>
/// Interface for Dead Letter Queue operations
/// </summary>
public interface IAuditDeadLetterQueue
{
    /// <summary>
    /// Stores a failed audit event to the dead letter queue
    /// </summary>
    Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null);
    
    /// <summary>
    /// Stores a failed audit entity to the dead letter queue
    /// </summary>
    Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null, string? reason = null);
    
    /// <summary>
    /// Retrieves all failed events from the dead letter queue
    /// </summary>
    Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100);
    
    /// <summary>
    /// Retrieves failed events by date range
    /// </summary>
    Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate, DateTimeOffset endDate);
    
    /// <summary>
    /// Attempts to reprocess a failed event
    /// </summary>
    Task<bool> ReprocessEventAsync(string deadLetterId);
    
    /// <summary>
    /// Reprocesses all failed events
    /// </summary>
    Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Removes successfully processed events from the queue
    /// </summary>
    Task<int> PurgeProcessedEventsAsync();
    
    /// <summary>
    /// Gets statistics about the dead letter queue
    /// </summary>
    Task<DeadLetterStatistics> GetStatisticsAsync();
}