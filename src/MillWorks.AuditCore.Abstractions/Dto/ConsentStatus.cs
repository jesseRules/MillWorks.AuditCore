namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Result of a consent verification check.
/// </summary>
public enum ConsentStatus
{
    /// <summary>
    /// Active consent exists in the cache for the given user + entity type + scope.
    /// </summary>
    Granted,

    /// <summary>
    /// No consent record found in the cache. This does NOT mean consent was denied —
    /// it means the cache has no entry (cold cache, never recorded, or evicted).
    /// In Enforce mode, this blocks the operation (fail-closed).
    /// </summary>
    NotFound
}
