using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Services;

/// <summary>
/// Dead letter audit scope - used when scope creation fails
/// </summary>
public sealed class DeadLetterAuditScope(
    string eventType,
    object? target,
    IAuditDeadLetterQueue deadLetterQueue,
    ILogger logger)
    : ICustomAuditScope
{
    /// <summary>
    /// Disposed flag
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Event representing the audit scope
    /// </summary>
    public AuditEvent Event { get; } = new()
    {
        EventId = Guid.NewGuid(),
        EventType = eventType,
        StartDate = DateTimeOffset.UtcNow,
        Target = target != null ? new AuditTarget { New = target } : null
    };

    /// <summary>
    /// Sets a custom field on the audit event
    /// </summary>
    /// <param name="fieldName"></param>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    public void SetCustomField<T>(string fieldName, T value)
    {
        Event.CustomFields[fieldName] = value;
    }

    /// <summary>
    /// Sets the target object for the audit event
    /// </summary>
    /// <param name="target"></param>
    public void SetTarget(object target)
    {
        Event.Target = new AuditTarget { New = target };
    }

    /// <summary>
    /// Saves the audit scope directly to the dead letter queue
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Event.EndDate = DateTimeOffset.UtcNow;
        Event.CalculateDuration();

        await deadLetterQueue.StoreFailedEventAsync(
            Event,
            null,
            "Direct to DLQ - scope creation failed");

        logger.LogWarning("Audit event {EventId} saved directly to DLQ", Event.EventId);
    }

    /// <summary>
    /// Disposes the audit scope synchronously.
    /// Sync Dispose is a no-op to avoid deadlocks from sync-over-async patterns.
    /// Use 'await using' or DisposeAsync() for the event to be saved to DLQ.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        logger.LogWarning(
            "DeadLetterAuditScope.Dispose() called synchronously for event {EventId}. " +
            "The audit event will not be saved to DLQ. Use 'await using' or DisposeAsync() instead.",
            Event.EventId);
    }

    /// <summary>
    /// Disposes the audit scope asynchronously, ensuring resources are cleaned up
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            await SaveAsync();
            _disposed = true;
        }
    }
}