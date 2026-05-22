using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Custom audit scope implementation without Audit.NET
/// </summary>
public sealed class CustomAuditScope(AuditEvent auditEvent, IAuditLogger auditLogger, ILogger logger) : ICustomAuditScope
{
    /// <summary>
    /// Disposed flag
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Dispose timeout duration
    /// </summary>
    private static readonly TimeSpan _disposeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Event being audited
    /// </summary>
    public AuditEvent Event { get; } = auditEvent;

    /// <summary>
    /// Sets a custom field in the audit event
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
    /// Saves the audit event asynchronously
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;

        Event.EndDate = DateTimeOffset.UtcNow;
        Event.CalculateDuration();
        await auditLogger.LogAsync(Event, cancellationToken);

        _disposed = true;
    }

    /// <summary>
    /// Disposes the audit scope synchronously.
    /// All callers in this codebase use <c>await using</c> (DisposeAsync).
    /// Sync Dispose is a no-op to avoid deadlocks from sync-over-async patterns.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        logger.LogWarning(
            "CustomAuditScope.Dispose() called synchronously for event {EventId}. " +
            "The audit event will not be saved. Use 'await using' or DisposeAsync() instead.",
            Event.EventId);
    }

    /// <summary>
    /// Disposes the audit scope asynchronously
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            // Use a CancellationTokenSource with timeout for async disposal too
            using var cts = new CancellationTokenSource(_disposeTimeout);
            await SaveAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning(
                "Audit scope async disposal timed out for event {EventId}",
                Event.EventId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Error during audit scope async disposal for event {EventId}",
                Event.EventId);
        }
        finally
        {
            _disposed = true;
        }
    }
}