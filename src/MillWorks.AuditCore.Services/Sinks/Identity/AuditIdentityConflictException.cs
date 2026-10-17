namespace MillWorks.AuditCore.Services.Sinks.Identity;

/// <summary>Payload-safe failure for caller-supplied identities. No existing payload, tenant,
/// fingerprint or owner metadata is disclosed. Unverified historical content is not a duplicate.</summary>
public sealed class AuditIdentityConflictException(IReadOnlyList<Guid> logicalIds, bool unverified = false)
    : Exception(unverified ? "audit_identity_unverified" : "audit_identity_conflict")
{
    public IReadOnlyList<Guid> LogicalIds { get; } = logicalIds;
    public bool Unverified { get; } = unverified;
}
