using System.Text.Json.Serialization;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// Stable, non-payload identity used by retention and legal-hold consumers.
/// AuditCore records the identity but does not interpret resource-type keys or depend on a
/// governance/hold implementation.
/// </summary>
public sealed record AuditGovernanceIdentity
{
    /// <summary>Creates a tenant-scoped governance identity.</summary>
    public AuditGovernanceIdentity(Guid tenantId, string? resourceType = null, Guid? resourceId = null)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant identity must be non-empty.", nameof(tenantId));

        string? normalizedResourceType = string.IsNullOrWhiteSpace(resourceType)
            ? null
            : resourceType.Trim();

        if ((normalizedResourceType is null) != (resourceId is null))
            throw new ArgumentException("Resource type and resource id must either both be supplied or both be absent.");
        if (resourceId == Guid.Empty)
            throw new ArgumentException("Resource identity must be non-empty.", nameof(resourceId));

        TenantId = tenantId;
        ResourceType = normalizedResourceType;
        ResourceId = resourceId;
    }

    /// <summary>Tenant that owns the audited evidence.</summary>
    [JsonPropertyName("tenant_id")]
    public Guid TenantId { get; }

    /// <summary>Optional host-defined canonical resource-type key.</summary>
    [JsonPropertyName("resource_type")]
    public string? ResourceType { get; }

    /// <summary>Optional canonical resource identifier paired with <see cref="ResourceType"/>.</summary>
    [JsonPropertyName("resource_id")]
    public Guid? ResourceId { get; }
}
