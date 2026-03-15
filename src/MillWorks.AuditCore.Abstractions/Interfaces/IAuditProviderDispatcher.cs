namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Dispatches audit events to registered providers after entity changes are saved.
/// Implemented in Services, resolved by the interceptor via ScopedServiceProvider.
/// </summary>
public interface IAuditProviderDispatcher
{
    /// <summary>
    /// Dispatches pending provider actions for entities that were just saved.
    /// </summary>
    Task DispatchAsync(IReadOnlyList<PendingProviderDispatch> dispatches, CancellationToken cancellationToken);
}

/// <summary>
/// Captures entity change information for deferred provider dispatch after save.
/// </summary>
public sealed record PendingProviderDispatch(
    string EntityTypeName,
    string Action,
    object Entity,
    object? OldValues);
