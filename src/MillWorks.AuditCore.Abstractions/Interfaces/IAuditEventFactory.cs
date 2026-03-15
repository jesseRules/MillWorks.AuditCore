
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Interface for audit event factory
/// </summary>
public interface IAuditEventFactory
{
    /// <summary>
    /// Create a new audit event with standard fields populated
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="target"></param>
    /// <param name="callerMemberName">Automatically populated with the caller's method name</param>
    /// <returns></returns>
    AuditEvent CreateEvent(string eventType, object? target = null,
        [System.Runtime.CompilerServices.CallerMemberName] string callerMemberName = "");

    /// <summary>
    /// Create a new audit event with standard fields populated
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="action"></param>
    /// <param name="entity"></param>
    /// <param name="oldValues"></param>
    /// <returns></returns>
    AuditEvent CreateEntityEvent(string entityType, string action, object entity, object? oldValues = null);

    /// <summary>
    /// Create an audit event for entity changes with additional metadata
    /// </summary>
    /// <param name="operationType"></param>
    /// <param name="operationId"></param>
    /// <param name="status"></param>
    /// <param name="metadata"></param>
    /// <returns></returns>
    AuditEvent CreateOperationEvent(string operationType, Guid operationId, string status, object? metadata = null);
}
