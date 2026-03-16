namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Singleton mapping of entity type names to their audit provider types.
/// Used by the interceptor to determine which provider handles which entity.
/// </summary>
public sealed class AuditProviderTypeMap
{
    /// <summary>
    /// Internal mapping of entity type name to provider type.
    /// </summary>
    private readonly Dictionary<string, Type> _map = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registers a provider type for the given entity type name.
    /// </summary>
    public void Register(string entityType, Type providerType) => _map[entityType] = providerType;

    /// <summary>
    /// Gets the provider type for the given entity type name, or null if none registered.
    /// </summary>
    public Type? GetProviderType(string entityType) => _map.GetValueOrDefault(entityType);

    /// <summary>
    /// Returns true if a provider is registered for the given entity type name.
    /// </summary>
    public bool HasProvider(string entityType) => _map.ContainsKey(entityType);
}
