using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Validators.Interfaces;

namespace MillWorks.AuditCore.Services.Validators;

/// <summary>
/// Scans assemblies for [FERPA] and [SensitiveData] attributes.
/// All caches are populated eagerly in the constructor — no lazy loading.
/// </summary>
public sealed class ComplianceAttributeScanner : IComplianceAttributeScanner
{
    private readonly IReadOnlyList<Type> _ferpaEntities;
    private readonly ConcurrentDictionary<ComplianceStandard, IReadOnlyList<SensitivePropertyInfo>> _byStandard;
    private readonly ConcurrentDictionary<Type, IReadOnlyList<SensitivePropertyInfo>> _byType;

    /// <summary>
    /// Creates a new scanner and eagerly populates all caches.
    /// </summary>
    /// <param name="assemblies">
    /// Explicit list of assemblies to scan. If empty, falls back to AppDomain.CurrentDomain.GetAssemblies()
    /// with a logged warning (explicit registration is recommended for deterministic results).
    /// </param>
    /// <param name="logger">Optional logger.</param>
    public ComplianceAttributeScanner(
        IEnumerable<Assembly>? assemblies = null,
        ILogger<ComplianceAttributeScanner>? logger = null)
    {
        var assemblyList = assemblies?.ToList();

        if (assemblyList is null or { Count: 0 })
        {
            logger?.LogWarning(
                "ComplianceAttributeScanner: No assemblies explicitly provided. " +
                "Falling back to AppDomain.CurrentDomain.GetAssemblies(). " +
                "For deterministic results, use .ScanAssemblies() in UseCompliance() configuration");
            assemblyList = AppDomain.CurrentDomain.GetAssemblies()
                .Where(static a => !a.IsDynamic && a.GetName().Name?.StartsWith("MillWorks") == true)
                .ToList();
        }

        var allTypes = assemblyList
            .SelectMany(static a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException ex)
                {
                    return ex.Types.Where(static t => t != null).Cast<Type>();
                }
            })
            .ToList();

        // Scan for [FERPA] entities
        _ferpaEntities = allTypes
            .Where(static t => t.GetCustomAttribute<FERPAAttribute>() is not null)
            .ToList()
            .AsReadOnly();

        // Scan for [SensitiveData] properties
        var allSensitiveProperties = new List<SensitivePropertyInfo>();
        foreach (var type in allTypes!)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var attr = prop.GetCustomAttribute<SensitiveDataAttribute>();
                if (attr is null) continue;

                allSensitiveProperties.Add(new SensitivePropertyInfo(
                    DeclaringType: type,
                    Property: prop,
                    ApplicableStandards: attr.ApplicableStandards,
                    AutoEncrypt: attr.AutoEncrypt,
                    MaskInLogs: attr.MaskInLogs));
            }
        }

        // Index by standard
        _byStandard = new ConcurrentDictionary<ComplianceStandard, IReadOnlyList<SensitivePropertyInfo>>();
        foreach (var standard in Enum.GetValues<ComplianceStandard>())
        {
            var matching = allSensitiveProperties
                .Where(p => p.ApplicableStandards.Contains(standard))
                .ToList()
                .AsReadOnly();
            _byStandard[standard] = matching;
        }

        // Index by declaring type
        _byType = new ConcurrentDictionary<Type, IReadOnlyList<SensitivePropertyInfo>>();
        foreach (var group in allSensitiveProperties.GroupBy(static p => p.DeclaringType))
        {
            _byType[group.Key] = group.ToList().AsReadOnly();
        }

        logger?.LogInformation(
            "ComplianceAttributeScanner initialized: {FerpaEntityCount} [FERPA] entities, " +
            "{SensitivePropertyCount} [SensitiveData] properties across {AssemblyCount} assemblies",
            _ferpaEntities.Count, allSensitiveProperties.Count, assemblyList.Count);
    }

    /// <inheritdoc />
    public IReadOnlyList<Type> GetFerpaEntities() => _ferpaEntities;

    /// <inheritdoc />
    public IReadOnlyList<SensitivePropertyInfo> GetSensitiveProperties(ComplianceStandard standard)
        => _byStandard.GetValueOrDefault(standard, []);

    /// <inheritdoc />
    public IReadOnlyList<SensitivePropertyInfo> GetSensitiveProperties(Type entityType)
        => _byType.GetValueOrDefault(entityType, []);
}
