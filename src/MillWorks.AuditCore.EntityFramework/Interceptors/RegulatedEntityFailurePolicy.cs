using System.Collections.Concurrent;
using System.Reflection;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Interceptors;

/// <summary>
/// Default <see cref="IAuditFailurePolicy"/>. Under <see cref="AuditFailureMode.FailClosedForRegulated"/>,
/// considers an entity regulated when its class carries <c>[FERPA]</c> or <c>[PHI]</c>, or when any
/// public instance property carries <c>[SensitiveData]</c> with <c>ApplicableStandards</c> containing
/// HIPAA, FERPA, GDPR, or PCI_DSS. Under <see cref="AuditFailureMode.Permissive"/> always allows;
/// under <see cref="AuditFailureMode.FailClosedAlways"/> always fails closed.
/// </summary>
public sealed class RegulatedEntityFailurePolicy : IAuditFailurePolicy
{
    private static readonly HashSet<ComplianceStandard> _regulatedStandards =
    [
        ComplianceStandard.HIPAA,
        ComplianceStandard.FERPA,
        ComplianceStandard.GDPR,
        ComplianceStandard.PCI_DSS
    ];

    private static readonly ConcurrentDictionary<Type, bool> _regulatedCache = new();

    /// <inheritdoc />
    public bool ShouldFailClosed(AuditFailureContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.FailureMode switch
        {
            AuditFailureMode.Permissive => false,
            AuditFailureMode.FailClosedAlways => true,
            AuditFailureMode.FailClosedForRegulated => context.Entities.Any(static e => IsRegulated(e.EntityType)),
            _ => false
        };
    }

    private static bool IsRegulated(Type entityType) =>
        _regulatedCache.GetOrAdd(entityType, static t =>
        {
            if (t.GetCustomAttribute<FERPAAttribute>() is not null) return true;
            if (t.GetCustomAttribute<PHIAttribute>() is not null) return true;

            foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var sensitive = prop.GetCustomAttribute<SensitiveDataAttribute>();
                if (sensitive is null) continue;

                if (sensitive.ApplicableStandards is { Length: > 0 } standards &&
                    standards.Any(static s => _regulatedStandards.Contains(s)))
                {
                    return true;
                }
            }

            return false;
        });
}
