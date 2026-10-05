namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Consumer policy that declares which typed EF properties identify data subjects. AuditCore reads
/// those properties through EF metadata; it never searches snapshots, descriptions, or arbitrary JSON.
/// </summary>
public interface IAuditSubjectIdentityPolicy
{
    /// <summary>
    /// Returns the EF property names that hold subject identifiers for the supplied entity type.
    /// Values must be non-empty <see cref="Guid"/> values when present.
    /// </summary>
    IReadOnlyCollection<string> GetSubjectPropertyNames(Type entityType);
}
