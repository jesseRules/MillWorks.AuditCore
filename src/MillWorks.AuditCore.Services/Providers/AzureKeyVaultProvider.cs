using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Encryption.Providers;

/// <summary>
/// Encryption key provider using Azure Key Vault
/// Recommended for production cloud deployments
/// </summary>
public sealed class AzureKeyVaultProvider(
    string keyVaultUrl,
    ILogger<AzureKeyVaultProvider> logger)
    : IEncryptionKeyProvider
{
    /// <summary>
    /// Secret client for Azure Key Vault
    /// </summary>
    private readonly SecretClient _secretClient = new(
        new Uri(keyVaultUrl),
        new DefaultAzureCredential());

    /// <summary>
    /// Key cache to minimize Key Vault calls, with expiration timestamps
    /// </summary>
    private readonly ConcurrentDictionary<string, (byte[] Key, DateTimeOffset CachedAt)> _keyCache = new();

    /// <summary>
    /// Cache expiration duration
    /// </summary>
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);

    /// <summary>
    /// Current key version secret name
    /// </summary>
    private const string CurrentVersionKey = "audit-encryption-current-version";

    /// <summary>
    /// Key prefix in Key Vault
    /// </summary>
    private const string KeyPrefix = "audit-encryption-key";

    /// <inheritdoc />
    public async Task<byte[]> GetEncryptionKeyAsync(string fieldName, CancellationToken cancellationToken = default)
    {
        var keyVersion = await GetCurrentKeyVersionAsync(cancellationToken);
        return await GetEncryptionKeyAsync(fieldName, keyVersion, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]> GetEncryptionKeyAsync(string fieldName, string keyVersion,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"{fieldName}:{keyVersion}";

        if (TryGetCachedKey(cacheKey, out var cachedKey))
            return cachedKey;

        try
        {
            var secretName = $"{KeyPrefix}-{keyVersion}";
            var secret = await _secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            return CacheKeyFromSecret(secret.Value.Value, cacheKey, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve encryption key from Key Vault for field {FieldName}",
                fieldName);
            throw new KeyProviderException(
                $"Failed to retrieve encryption key for field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var versionSecret =
                await _secretClient.GetSecretAsync(CurrentVersionKey, cancellationToken: cancellationToken);
            return versionSecret.Value.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve current key version from Key Vault");
            throw new KeyProviderException("Failed to retrieve current key version", ex);
        }
    }

    /// <inheritdoc />
    public byte[] GetEncryptionKey(string fieldName)
    {
        var keyVersion = GetCurrentKeyVersion();
        return GetEncryptionKey(fieldName, keyVersion);
    }

    /// <inheritdoc />
    public byte[] GetEncryptionKey(string fieldName, string keyVersion)
    {
        var cacheKey = $"{fieldName}:{keyVersion}";

        if (TryGetCachedKey(cacheKey, out var cachedKey))
            return cachedKey;

        try
        {
            var secretName = $"{KeyPrefix}-{keyVersion}";
            var secret = _secretClient.GetSecret(secretName);
            return CacheKeyFromSecret(secret.Value.Value, cacheKey, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve encryption key from Key Vault for field {FieldName}",
                fieldName);
            throw new KeyProviderException(
                $"Failed to retrieve encryption key for field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public string GetCurrentKeyVersion()
    {
        try
        {
            var versionSecret = _secretClient.GetSecret(CurrentVersionKey);
            return versionSecret.Value.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to retrieve current key version from Key Vault");
            throw new KeyProviderException("Failed to retrieve current key version", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> RotateKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate new master key
            var newMasterKey = new byte[32]; // 256 bits for AES-256
            RandomNumberGenerator.Fill(newMasterKey);

            // Create new version identifier
            var newVersion = $"v{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

            // Store new master key in Key Vault
            var secretName = $"{KeyPrefix}-{newVersion}";
            await _secretClient.SetSecretAsync(
                secretName,
                Convert.ToBase64String(newMasterKey), cancellationToken);

            // Update current version pointer
            await _secretClient.SetSecretAsync(CurrentVersionKey, newVersion, cancellationToken);

            // Clear cache to force reload
            _keyCache.Clear();

            logger.LogInformation("Successfully rotated encryption keys to version {Version}",
                newVersion);

            return newVersion;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to rotate encryption keys");
            throw new KeyProviderException("Failed to rotate encryption keys", ex);
        }
    }

    private bool TryGetCachedKey(string cacheKey, out byte[] key)
    {
        if (_keyCache.TryGetValue(cacheKey, out var cached) &&
            DateTimeOffset.UtcNow - cached.CachedAt < _cacheExpiration)
        {
            key = cached.Key;
            return true;
        }

        key = default!;
        return false;
    }

    private byte[] CacheKeyFromSecret(string secretValue, string cacheKey, string fieldName, string keyVersion)
    {
        var masterKey = Convert.FromBase64String(secretValue);
        var derivedKey = DeriveFieldKey(masterKey, fieldName);
        CryptographicOperations.ZeroMemory(masterKey);

        _keyCache[cacheKey] = (derivedKey, DateTimeOffset.UtcNow);

        logger.LogDebug("Retrieved encryption key for field {FieldName} version {KeyVersion}",
            fieldName, keyVersion);

        return derivedKey;
    }

    /// <summary>
    /// Derives a field-specific key from the master key using HKDF
    /// </summary>
    private static byte[] DeriveFieldKey(byte[] masterKey, string fieldName)
    {
        var info = Encoding.UTF8.GetBytes($"field:{fieldName}");
        var salt = new byte[32]; // Empty salt for deterministic derivation
        var derivedKey = new byte[32]; // 256 bits

        // Use .NET 9's built-in HKDF static method
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, salt, info);

        return derivedKey;
    }
}