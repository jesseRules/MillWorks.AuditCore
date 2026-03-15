using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Providers.Base;

namespace MillWorks.AuditCore.AspNetCore.Configuration;

/// <summary>
/// Provider registry for audit providers.
/// Maps entity type names to provider types and registers them in DI.
/// </summary>
public sealed class ProviderRegistry(IServiceCollection services, AuditProviderTypeMap map)
{
    /// <summary>
    /// Adds an audit provider for a specific entity type
    /// </summary>
    public ProviderRegistry AddProvider<T>(string entityType) where T : class, IAuditProvider
    {
        services.AddScoped<T>();
        map.Register(entityType, typeof(T));
        return this;
    }
}
