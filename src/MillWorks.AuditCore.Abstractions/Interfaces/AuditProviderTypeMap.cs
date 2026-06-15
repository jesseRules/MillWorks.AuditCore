using System.Collections.Frozen;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Singleton mapping of entity type names to their audit provider types.
/// Used by the interceptor to determine which provider handles which entity.
/// Call <see cref="Freeze"/> after startup to prevent further modifications.
/// </summary>
public sealed class AuditProviderTypeMap
{
    private Dictionary<string, Type>? _mutableMap = new(StringComparer.OrdinalIgnoreCase);
    private FrozenDictionary<string, Type>? _frozenMap;

    /// <summary>
    /// Registers a provider type for the given entity type name.
    /// Must be called before <see cref="Freeze"/>.
    /// </summary>
    public void Register(string entityType, Type providerType)
    {
        if (_mutableMap is null)
            throw new InvalidOperationException(
                "AuditProviderTypeMap has been frozen; Register must be called during startup.");

        _mutableMap[entityType] = providerType;
    }

    /// <summary>
    /// Freezes the map, preventing further modifications and enabling lock-free reads.
    /// Called automatically by the host after configuration completes.
    /// </summary>
    public void Freeze()
    {
        if (_mutableMap is null)
            return;

        _frozenMap = _mutableMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _mutableMap = null;
    }

    /// <summary>
    /// Gets the provider type for the given entity type name, or null if none registered.
    /// </summary>
    public Type? GetProviderType(string entityType)
    {
        if (_frozenMap is not null)
            return _frozenMap.GetValueOrDefault(entityType);

        return _mutableMap?.GetValueOrDefault(entityType);
    }

    /// <summary>
    /// Returns true if a provider is registered for the given entity type name.
    /// </summary>
    public bool HasProvider(string entityType)
    {
        if (_frozenMap is not null)
            return _frozenMap.ContainsKey(entityType);

        return _mutableMap?.ContainsKey(entityType) ?? false;
    }
}
