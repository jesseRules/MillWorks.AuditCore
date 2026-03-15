using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Interface for custom audit scope
/// </summary>
public interface ICustomAuditScope : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Event associated with this scope
    /// </summary>
    AuditEvent Event { get; }
    
    /// <summary>
    /// Sets a custom field on the audit event
    /// </summary>
    /// <param name="fieldName"></param>
    /// <param name="value"></param>
    /// <typeparam name="T"></typeparam>
    void SetCustomField<T>(string fieldName, T value);
    
    /// <summary>
    /// Sets the target object for the audit event
    /// </summary>
    /// <param name="target"></param>
    void SetTarget(object target);
    
    /// <summary>
    /// Saves the audit event to the underlying storage
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SaveAsync(CancellationToken cancellationToken = default);
}