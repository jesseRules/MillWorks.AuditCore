namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Consumer policy that maps an audited CLR entity type to a stable canonical resource-type key.
/// Tenant and primary-key values are read from EF metadata by AuditCore; policies never inspect
/// entity values and must be pure because the audit interceptor is singleton-scoped.
/// </summary>
public interface IAuditResourceIdentityPolicy
{
    /// <summary>Returns the canonical key for the entity type, or null when no exact-resource identity is published.</summary>
    string? GetResourceType(Type entityType);
}
