using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Providers.Base;

/// <summary>
/// Interface for audit providers that handle specific entity types
/// </summary>
public interface IAuditProvider
{
    /// <summary>
    /// Entity type that this provider handles
    /// </summary>
    string EntityType { get; }

    /// <summary>
    /// Creates an audit event for the specified action and entity
    /// </summary>
    Task<AuditEvent> CreateAuditEventAsync(string action, object? entity, object? oldValues = null);

    /// <summary>
    /// Determines if the action should be audited for the given entity
    /// </summary>
    Task<bool> ShouldAuditAsync(string action, object entity);

    /// <summary>
    /// Enriches the audit event with additional entity-specific information
    /// </summary>
    Task EnrichAuditEventAsync(AuditEvent auditEvent, object? entity);

    /// <summary>
    /// Gets the changes between old and new values
    /// </summary>
    Dictionary<string, object?> GetChanges(object? oldValues, object? newValues);
}