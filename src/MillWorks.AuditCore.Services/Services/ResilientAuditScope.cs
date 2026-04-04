using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;

namespace MillWorks.AuditCore.Services.DeadLetterQueue.Services;

/// <summary>
/// Resilient audit scope with DLQ support
/// </summary>
public sealed class ResilientAuditScope(
    ICustomAuditScope innerScope,
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
    public AuditEvent Event => innerScope.Event;

    /// <summary>
    /// Sets a custom field on the audit event
    /// </summary>
    /// <param name="fieldName"></param>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    public void SetCustomField<T>(string fieldName, T value)
    {
        innerScope.SetCustomField(fieldName, value);
    }

    /// <summary>
    /// Sets the target object for the audit event
    /// </summary>
    /// <param name="target"></param>
    public void SetTarget(object target)
    {
        innerScope.SetTarget(target);
    }

    /// <summary>
    /// Saves the audit scope, with DLQ fallback on failure
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await innerScope.SaveAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save audit scope");

            // Send to DLQ
            await deadLetterQueue.StoreFailedEventAsync(
                Event,
                ex,
                "Failed to save audit scope");
        }
    }

    /// <summary>
    /// Disposes the audit scope, ensuring resources are cleaned up
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;

        try
        {
            innerScope.Dispose();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispose audit scope. " +
                "DLQ storage skipped in sync Dispose path; prefer DisposeAsync for DLQ support.");
        }
        finally
        {
            _disposed = true;
        }
    }

    /// <summary>
    /// Disposes the audit scope asynchronously, ensuring resources are cleaned up
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        try
        {
            await innerScope.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to dispose audit scope asynchronously");
            await deadLetterQueue.StoreFailedEventAsync(Event, ex, "Failed to dispose scope");
        }
        finally
        {
            _disposed = true;
        }
    }
}