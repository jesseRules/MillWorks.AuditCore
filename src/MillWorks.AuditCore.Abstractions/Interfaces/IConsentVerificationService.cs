using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Cache-backed consent verification for FERPA compliance enforcement.
/// All reads are synchronous (IMemoryCache) so they are safe inside SaveChanges interceptors.
/// <para>
/// <b>Architectural constraint:</b> This service never queries the database. Consent enters the cache
/// via (1) startup warmup (<c>WarmConsentCacheOnStartup</c>) or (2) explicit <see cref="RecordConsentAsync"/> calls.
/// A cache miss returns <see cref="ConsentStatus.NotFound"/>, not "go check the DB".
/// </para>
/// </summary>
public interface IConsentVerificationService
{
    /// <summary>
    /// Synchronously check if active consent exists for a user + entity type + scope.
    /// Reads from <c>IMemoryCache</c> — safe for both sync and async interceptor paths.
    /// </summary>
    /// <param name="userId">The user whose consent is being verified.</param>
    /// <param name="entityType">The entity type name (e.g., "StudentEntity").</param>
    /// <param name="scope">Optional scope qualifier. Null means any/default scope.</param>
    /// <returns><see cref="ConsentStatus.Granted"/> if cached; <see cref="ConsentStatus.NotFound"/> otherwise.</returns>
    ConsentStatus HasActiveConsent(string userId, string entityType, string? scope = null);

    /// <summary>
    /// Async convenience wrapper around <see cref="HasActiveConsent"/>. No async I/O — returns synchronously.
    /// </summary>
    Task<ConsentStatus> HasActiveConsentAsync(string userId, string entityType, string? scope = null);

    /// <summary>
    /// Record a consent grant. Updates the in-memory cache.
    /// <para>
    /// <b>FERPA consent semantics:</b> FERPA consent does not expire — it is valid until explicitly revoked.
    /// The <paramref name="expiresAt"/> parameter controls the <em>cache TTL</em>, not consent expiration.
    /// For consent with no natural expiry, pass <see cref="DateTimeOffset.MaxValue"/> (the cache entry
    /// effectively never expires). Explicit revocation via <see cref="RevokeConsentAsync"/> removes the
    /// entry regardless of TTL.
    /// </para>
    /// </summary>
    /// <param name="userId">The user granting consent.</param>
    /// <param name="entityType">The entity type consent applies to.</param>
    /// <param name="scope">Optional scope qualifier.</param>
    /// <param name="expiresAt">Cache TTL expiration. Use <see cref="DateTimeOffset.MaxValue"/> for non-expiring consent.</param>
    Task RecordConsentAsync(string userId, string entityType, string? scope, DateTimeOffset expiresAt);

    /// <summary>
    /// Revoke consent. Removes the cache entry immediately regardless of TTL.
    /// </summary>
    /// <param name="userId">The user revoking consent.</param>
    /// <param name="entityType">The entity type to revoke consent for.</param>
    /// <param name="scope">Optional scope qualifier. Null means any/default scope.</param>
    Task RevokeConsentAsync(string userId, string entityType, string? scope = null);
}
