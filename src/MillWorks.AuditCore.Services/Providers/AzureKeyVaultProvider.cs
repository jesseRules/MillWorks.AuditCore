using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Azure;
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
    : IEncryptionKeyProvider, IDisposable
{
    /// <summary>
    /// Secret client for Azure Key Vault
    /// </summary>
    private readonly SecretClient _secretClient = CreateSecretClient(keyVaultUrl);

    /// <summary>
    /// Key cache to minimize Key Vault calls, with expiration timestamps
    /// </summary>
    private readonly ConcurrentDictionary<string, CacheEntry<byte[]>> _keyCache = new();

    /// <summary>
    /// In-flight async key loads to avoid duplicate concurrent Key Vault requests.
    /// </summary>
    private readonly ConcurrentDictionary<string, Lazy<Task<byte[]>>> _inflightKeyLoads = new();

    /// <summary>
    /// Cached current key version.
    /// </summary>
    private CacheEntry<string>? _currentVersionCache;

    /// <summary>
    /// In-flight async current-version load.
    /// </summary>
    private Lazy<Task<string>>? _inflightCurrentVersionLoad;

    /// <summary>
    /// Guard for current-version cache access.
    /// </summary>
    private readonly Lock _currentVersionLock = new();

    /// <summary>
    /// Cache expiration duration
    /// </summary>
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(1);

    /// <summary>
    /// Time provider for testable cache expiry.
    /// </summary>
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// Disposal flag.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Current key version secret name
    /// </summary>
    private const string _currentVersionKey = "audit-encryption-current-version";

    /// <summary>
    /// Key prefix in Key Vault
    /// </summary>
    private const string _keyPrefix = "audit-encryption-key";

    internal AzureKeyVaultProvider(
        SecretClient secretClient,
        ILogger<AzureKeyVaultProvider> logger,
        TimeProvider? timeProvider = null,
        TimeSpan? cacheExpiration = null)
        : this("https://placeholder.vault.azure.net/", logger)
    {
        _secretClient = secretClient ?? throw new ArgumentNullException(nameof(secretClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cacheExpiration = cacheExpiration ?? TimeSpan.FromHours(1);
    }

    /// <inheritdoc />
    public async Task<byte[]> GetEncryptionKeyAsync(string fieldName, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var keyVersion = await GetCurrentKeyVersionAsync(cancellationToken);
        return await GetEncryptionKeyAsync(fieldName, keyVersion, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<byte[]> GetEncryptionKeyAsync(string fieldName, string keyVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);

        var cacheKey = $"{fieldName}:{keyVersion}";

        if (TryGetCachedKey(cacheKey, out var cachedKey))
            return cachedKey;

        var lazyLoad = _inflightKeyLoads.GetOrAdd(cacheKey,
            _ => new Lazy<Task<byte[]>>(
                () => LoadAndCacheKeyAsync(fieldName, keyVersion, cacheKey, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazyLoad.Value;
        }
        finally
        {
            _inflightKeyLoads.TryRemove(cacheKey, out _);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (TryGetCachedCurrentVersion(out var cachedVersion))
            return cachedVersion;

        Lazy<Task<string>> lazyLoad;
        lock (_currentVersionLock)
        {
            if (TryGetCachedCurrentVersion(out cachedVersion))
                return cachedVersion;

            _inflightCurrentVersionLoad ??= new Lazy<Task<string>>(
                () => LoadAndCacheCurrentVersionAsync(cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);

            lazyLoad = _inflightCurrentVersionLoad;
        }

        try
        {
            return await lazyLoad.Value;
        }
        finally
        {
            lock (_currentVersionLock)
            {
                if (ReferenceEquals(_inflightCurrentVersionLoad, lazyLoad))
                    _inflightCurrentVersionLoad = null;
            }
        }
    }

    /// <inheritdoc />
    public byte[] GetEncryptionKey(string fieldName)
    {
        ThrowIfDisposed();
        var keyVersion = GetCurrentKeyVersion();
        return GetEncryptionKey(fieldName, keyVersion);
    }

    /// <inheritdoc />
    public byte[] GetEncryptionKey(string fieldName, string keyVersion)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyVersion);

        var cacheKey = $"{fieldName}:{keyVersion}";

        if (TryGetCachedKey(cacheKey, out var cachedKey))
            return cachedKey;

        try
        {
            var secretName = $"{_keyPrefix}-{keyVersion}";
            var secret = _secretClient.GetSecret(secretName);
            return CacheKeyFromSecret(GetRequiredSecretValue(secret.Value, fieldName, keyVersion), cacheKey, fieldName, keyVersion);
        }
        catch (RequestFailedException ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
        catch (FormatException ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
    }

    /// <inheritdoc />
    public string GetCurrentKeyVersion()
    {
        ThrowIfDisposed();

        if (TryGetCachedCurrentVersion(out var cachedVersion))
            return cachedVersion;

        try
        {
            var versionSecret = _secretClient.GetSecret(_currentVersionKey);
            var version = GetRequiredCurrentVersion(versionSecret.Value);
            _currentVersionCache = new CacheEntry<string>(version, _timeProvider.GetUtcNow());
            return version;
        }
        catch (RequestFailedException ex)
        {
            throw WrapCurrentVersionException(ex);
        }
        catch (Exception ex)
        {
            throw WrapCurrentVersionException(ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> RotateKeysAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            // Generate new master key
            var newMasterKey = new byte[32]; // 256 bits for AES-256
            RandomNumberGenerator.Fill(newMasterKey);

            // Create new version identifier
            var newVersion = $"v{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

            // Store new master key in Key Vault
            var secretName = $"{_keyPrefix}-{newVersion}";
            await _secretClient.SetSecretAsync(
                secretName,
                Convert.ToBase64String(newMasterKey), cancellationToken);

            // Update current version pointer
            await _secretClient.SetSecretAsync(_currentVersionKey, newVersion, cancellationToken);

            // Clear cache to force reload
            _keyCache.Clear();
            _currentVersionCache = null;

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

    /// <inheritdoc />
    public void Dispose()
    {
        _disposed = true;
        _keyCache.Clear();
        _currentVersionCache = null;
        _inflightKeyLoads.Clear();
    }

    private bool TryGetCachedKey(string cacheKey, out byte[] key)
    {
        if (_keyCache.TryGetValue(cacheKey, out var cached) &&
            _timeProvider.GetUtcNow() - cached.CachedAt < _cacheExpiration)
        {
            key = cached.Key;
            return true;
        }

        key = default!;
        return false;
    }

    private bool TryGetCachedCurrentVersion(out string version)
    {
        if (_currentVersionCache is not null &&
            _timeProvider.GetUtcNow() - _currentVersionCache.CachedAt < _cacheExpiration)
        {
            version = _currentVersionCache.Key;
            return true;
        }

        version = default!;
        return false;
    }

    private async Task<byte[]> LoadAndCacheKeyAsync(
        string fieldName,
        string keyVersion,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var secretName = $"{_keyPrefix}-{keyVersion}";
            var secret = await _secretClient.GetSecretAsync(secretName, null, null, cancellationToken);
            return CacheKeyFromSecret(
                GetRequiredSecretValue(secret.Value, fieldName, keyVersion),
                cacheKey,
                fieldName,
                keyVersion);
        }
        catch (RequestFailedException ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
        catch (FormatException ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            throw WrapSecretException(ex, fieldName, keyVersion);
        }
    }

    private async Task<string> LoadAndCacheCurrentVersionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var versionSecret =
                await _secretClient.GetSecretAsync(_currentVersionKey, null, null, cancellationToken);
            var version = GetRequiredCurrentVersion(versionSecret.Value);
            _currentVersionCache = new CacheEntry<string>(version, _timeProvider.GetUtcNow());
            return version;
        }
        catch (RequestFailedException ex)
        {
            throw WrapCurrentVersionException(ex);
        }
        catch (Exception ex)
        {
            throw WrapCurrentVersionException(ex);
        }
    }

    private byte[] CacheKeyFromSecret(string secretValue, string cacheKey, string fieldName, string keyVersion)
    {
        var masterKey = Convert.FromBase64String(secretValue);
        var derivedKey = DeriveFieldKey(masterKey, fieldName);
        CryptographicOperations.ZeroMemory(masterKey);

        _keyCache[cacheKey] = new CacheEntry<byte[]>(derivedKey, _timeProvider.GetUtcNow());

        logger.LogDebug("Retrieved encryption key for field {FieldName} version {KeyVersion}",
            fieldName, keyVersion);

        return derivedKey;
    }

    /// <summary>
    /// Fixed application-specific salt to avoid the degenerate all-zero HKDF salt case.
    /// Deterministic across all instances — maintains the deterministic derivation property.
    /// </summary>
    private static readonly byte[] _applicationSalt =
        SHA256.HashData("MillWorks.AuditCore.FieldKeyDerivation"u8);

    /// <summary>
    /// Derives a field-specific key from the master key using HKDF
    /// </summary>
    private static byte[] DeriveFieldKey(byte[] masterKey, string fieldName)
    {
        var info = Encoding.UTF8.GetBytes($"field:{fieldName}");
        var derivedKey = new byte[32]; // 256 bits

        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, _applicationSalt, info);

        return derivedKey;
    }

    private static SecretClient CreateSecretClient(string keyVaultUrl)
    {
        if (string.IsNullOrWhiteSpace(keyVaultUrl))
            throw new ArgumentException("Key Vault URL is required.", nameof(keyVaultUrl));

        if (!Uri.TryCreate(keyVaultUrl, UriKind.Absolute, out var uri))
            throw new ArgumentException("Key Vault URL must be a valid absolute URI.", nameof(keyVaultUrl));

        return new SecretClient(uri, new DefaultAzureCredential());
    }

    private static string GetRequiredSecretValue(KeyVaultSecret secret, string fieldName, string keyVersion)
    {
        if (string.IsNullOrWhiteSpace(secret.Value))
        {
            throw new KeyProviderException(
                $"Key Vault secret for field {fieldName} version {keyVersion} was empty.");
        }

        return secret.Value;
    }

    private static string GetRequiredCurrentVersion(KeyVaultSecret secret)
    {
        if (string.IsNullOrWhiteSpace(secret.Value))
            throw new KeyProviderException("Current key version secret was empty.");

        return secret.Value;
    }

    private KeyProviderException WrapSecretException(Exception ex, string fieldName, string keyVersion)
    {
        logger.LogError(ex, "Failed to retrieve encryption key from Key Vault for field {FieldName}",
            fieldName);

        return ex switch
        {
            KeyProviderException kpe => kpe,
            RequestFailedException { Status: 404 } => new KeyProviderException(
                $"Encryption key version '{keyVersion}' was not found for field {fieldName}.", ex),
            RequestFailedException { Status: 403 } => new KeyProviderException(
                $"Access denied retrieving encryption key for field {fieldName}.", ex),
            RequestFailedException => new KeyProviderException(
                $"Failed to retrieve encryption key for field {fieldName}.", ex),
            FormatException => new KeyProviderException(
                $"Encryption key material for field {fieldName} version {keyVersion} was invalid.", ex),
            _ => new KeyProviderException(
                $"Failed to retrieve encryption key for field {fieldName}.", ex)
        };
    }

    private KeyProviderException WrapCurrentVersionException(Exception ex)
    {
        logger.LogError(ex, "Failed to retrieve current key version from Key Vault");

        return ex switch
        {
            KeyProviderException kpe => kpe,
            RequestFailedException { Status: 404 } => new KeyProviderException(
                "Current key version secret was not found in Key Vault.", ex),
            RequestFailedException { Status: 403 } => new KeyProviderException(
                "Access denied retrieving current key version from Key Vault.", ex),
            _ => new KeyProviderException("Failed to retrieve current key version", ex)
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private sealed record CacheEntry<T>(T Key, DateTimeOffset CachedAt);
}
