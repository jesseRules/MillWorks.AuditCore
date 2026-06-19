using Microsoft.Extensions.Caching.Memory;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Compliance;

/// <summary>
/// Default <see cref="IConsentVerificationService"/> implementation backed by <see cref="IMemoryCache"/>.
/// Thread-safe. All reads are synchronous (cache-only, no DB fallback).
/// </summary>
public sealed class ConsentVerificationService(IMemoryCache cache) : IConsentVerificationService
{
    /// <summary>
    /// In-memory cache for storing consent records. The presence of a cache entry indicates active consent.
    /// </summary>
    private readonly IMemoryCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));

    /// <summary>
    /// Cache key prefix for FERPA consent entries.
    /// Format: "ferpa:consent:{userId}:{entityType}:{scope}"
    /// </summary>
    private const string _cacheKeyPrefix = "ferpa:consent:";

    /// <inheritdoc />
    public ConsentStatus HasActiveConsent(string userId, string entityType, string? scope = null)
    {
        var key = BuildCacheKey(userId, entityType, scope);
        return _cache.TryGetValue(key, out _) ? ConsentStatus.Granted : ConsentStatus.NotFound;
    }

    /// <inheritdoc />
    public Task<ConsentStatus> HasActiveConsentAsync(string userId, string entityType, string? scope = null) =>
        Task.FromResult(HasActiveConsent(userId, entityType, scope));

    /// <inheritdoc />
    public Task RecordConsentAsync(string userId, string entityType, string? scope, DateTimeOffset expiresAt)
    {
        var key = BuildCacheKey(userId, entityType, scope);

        var options = new MemoryCacheEntryOptions();

        // DateTimeOffset.MaxValue means "never expire" — don't set an absolute expiration
        // that would overflow. For all other values, set the cache TTL.
        if (expiresAt < DateTimeOffset.MaxValue)
        {
            options.SetAbsoluteExpiration(expiresAt);
        }

        // Store a sentinel value; the presence of the key is what matters.
        _cache.Set(key, true, options);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RevokeConsentAsync(string userId, string entityType, string? scope = null)
    {
        var key = BuildCacheKey(userId, entityType, scope);
        _cache.Remove(key);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds a deterministic cache key for a consent record.
    /// </summary>
    private static string BuildCacheKey(string userId, string entityType, string? scope) =>
        $"{_cacheKeyPrefix}{userId}:{entityType}:{scope ?? "*"}";
}
