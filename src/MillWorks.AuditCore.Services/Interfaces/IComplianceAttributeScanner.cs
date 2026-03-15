using System.Reflection;
using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Validators.Interfaces;

/// <summary>
/// Scans assemblies for compliance-related attributes ([FERPA], [SensitiveData]) and caches results.
/// Registered as a singleton — caches are populated eagerly in the constructor.
/// </summary>
public interface IComplianceAttributeScanner
{
    /// <summary>
    /// Returns all entity types decorated with [FERPA].
    /// </summary>
    IReadOnlyList<Type> GetFerpaEntities();

    /// <summary>
    /// Returns all properties decorated with [SensitiveData] where ApplicableStandards contains the given standard.
    /// </summary>
    IReadOnlyList<SensitivePropertyInfo> GetSensitiveProperties(ComplianceStandard standard);

    /// <summary>
    /// Returns all [SensitiveData]-decorated properties on the given entity type.
    /// </summary>
    IReadOnlyList<SensitivePropertyInfo> GetSensitiveProperties(Type entityType);
}

/// <summary>
/// Metadata about a property decorated with [SensitiveData].
/// </summary>
public sealed record SensitivePropertyInfo(
    Type DeclaringType,
    PropertyInfo Property,
    ComplianceStandard[] ApplicableStandards,
    bool AutoEncrypt,
    bool MaskInLogs);
