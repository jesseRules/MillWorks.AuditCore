namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Provides provider dispatch fields for the interceptor. Implemented by AuditDbContext;
/// consumer DbContexts that need provider dispatch support implement this interface.
/// When the saving DbContext does not implement this interface, provider dispatch silently no-ops.
/// </summary>
public interface IAuditProviderDispatchSource
{
    /// <summary>
    /// Scoped service provider for the current request. Used by the singleton interceptor
    /// to resolve scoped services (providers, loggers) via the DbContext instance.
    /// </summary>
    IServiceProvider? ScopedServiceProvider { get; }

    /// <summary>
    /// Re-entrancy guard — prevents infinite recursion if a provider triggers another save.
    /// </summary>
    bool IsDispatchingProviders { get; set; }

    /// <summary>
    /// Pending provider dispatches captured in SavingChanges, processed in SavedChanges.
    /// </summary>
    IReadOnlyList<PendingProviderDispatch>? PendingProviderDispatches { get; set; }
}
