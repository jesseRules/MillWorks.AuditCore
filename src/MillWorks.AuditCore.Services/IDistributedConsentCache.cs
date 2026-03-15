namespace MillWorks.AuditCore.Services.Compliance;

/// <summary>
/// Distributed consent cache abstraction for multi-instance deployments.
/// The default implementation uses <c>IMemoryCache</c> (single-process).
/// Swap this for a Redis-backed implementation when running multiple application instances.
/// <para>
/// <b>Future work:</b> This interface is defined for forward compatibility.
/// No distributed implementation is provided in this release.
/// </para>
/// </summary>
public interface IDistributedConsentCache
{
    /// <summary>
    /// Check if a consent entry exists in the distributed cache.
    /// </summary>
    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set a consent entry in the distributed cache.
    /// </summary>
    /// <param name="key">Cache key.</param>
    /// <param name="expiresAt">Absolute expiration. <see cref="DateTimeOffset.MaxValue"/> for non-expiring.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task SetAsync(string key, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove a consent entry from the distributed cache.
    /// </summary>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
