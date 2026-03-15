using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for logging audit events without Audit.NET dependencies
/// </summary>
public interface IAuditLogger
{
    /// <summary>
    /// Logs a custom audit event
    /// </summary>
    Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an audit event with the specified type and data
    /// </summary>
    Task LogAsync(string eventType, object? data = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Logs an audit event with the specified type, message, and data
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="message"></param>
    /// <param name="data"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LogAsync(string eventType, string message, Dictionary<string, object?> data,
        CancellationToken cancellationToken);

    /// <summary>
    /// Begins an operation and returns its ID
    /// </summary>
    Task<Guid> BeginOperationAsync(string operationType, object? metadata = null);

    /// <summary>
    /// Ends an operation with the specified ID
    /// </summary>
    Task EndOperationAsync(Guid operationId, bool success = true, object? result = null);

    /// <summary>
    /// Creates a new audit scope for the specified event type
    /// </summary>
    ICustomAuditScope CreateScope(string eventType, object? target = null);
}