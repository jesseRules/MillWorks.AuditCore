using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Encryption.Providers;

/// <summary>
/// File-based encryption key provider for DMZ/air-gapped deployments
/// Keys are stored encrypted on the file system
/// </summary>
public sealed class FileBasedKeyProvider : IEncryptionKeyProvider
{
    /// <summary>
    /// Key store directory path
    /// </summary>
    private readonly string _keyStorePath;

    /// <summary>
    /// Logger instance
    /// </summary>
    private readonly ILogger<FileBasedKeyProvider> _logger;

    /// <summary>
    /// Key cache for loaded encryption keys
    /// </summary>
    private readonly ConcurrentDictionary<string, byte[]> _keyCache;

    /// <summary>
    /// Master key for encrypting/decrypting stored keys
    /// </summary>
    private readonly byte[] _masterKey;

    /// <summary>
    /// Current version file name
    /// </summary>
    private const string CurrentVersionFile = "current-version.txt";

    /// <summary>
    /// Key file name pattern
    /// </summary>
    private const string KeyFilePattern = "key-{0}.encrypted";

    /// <summary>
    /// File-based key provider constructor
    /// </summary>
    /// <param name="keyStorePath"></param>
    /// <param name="masterKeyBase64"></param>
    /// <param name="logger"></param>
    public FileBasedKeyProvider(
        string keyStorePath,
        string masterKeyBase64,
        ILogger<FileBasedKeyProvider> logger)
    {
        _keyStorePath = keyStorePath;
        _logger = logger;
        _keyCache = new ConcurrentDictionary<string, byte[]>();
        _masterKey = Convert.FromBase64String(masterKeyBase64);

        // Ensure key store directory exists
        Directory.CreateDirectory(_keyStorePath);
    }

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

        if (_keyCache.TryGetValue(cacheKey, out var cachedKey))
            return cachedKey;

        try
        {
            var keyFilePath = Path.Combine(
                _keyStorePath,
                string.Format(KeyFilePattern, keyVersion));

            if (!File.Exists(keyFilePath))
            {
                throw new KeyProviderException(
                    $"Key file not found for version {keyVersion}");
            }

            var encryptedKeyData = await File.ReadAllBytesAsync(keyFilePath, cancellationToken);
            return LoadAndCacheKey(encryptedKeyData, cacheKey, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load encryption key for field {FieldName}", fieldName);
            throw new KeyProviderException(
                $"Failed to load encryption key for field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var versionFilePath = Path.Combine(_keyStorePath, CurrentVersionFile);

            if (!File.Exists(versionFilePath))
            {
                var initialVersion = await InitializeFirstKeyAsync(cancellationToken);
                return initialVersion;
            }

            return await File.ReadAllTextAsync(versionFilePath, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve current key version");
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

        if (_keyCache.TryGetValue(cacheKey, out var cachedKey))
            return cachedKey;

        try
        {
            var keyFilePath = Path.Combine(
                _keyStorePath,
                string.Format(KeyFilePattern, keyVersion));

            if (!File.Exists(keyFilePath))
            {
                throw new KeyProviderException(
                    $"Key file not found for version {keyVersion}");
            }

            var encryptedKeyData = File.ReadAllBytes(keyFilePath);
            return LoadAndCacheKey(encryptedKeyData, cacheKey, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load encryption key for field {FieldName}", fieldName);
            throw new KeyProviderException(
                $"Failed to load encryption key for field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public string GetCurrentKeyVersion()
    {
        try
        {
            var versionFilePath = Path.Combine(_keyStorePath, CurrentVersionFile);

            if (!File.Exists(versionFilePath))
            {
                return InitializeFirstKeySync();
            }

            return File.ReadAllText(versionFilePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve current key version");
            throw new KeyProviderException("Failed to retrieve current key version", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> RotateKeysAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Generate new encryption key
            var newKey = new byte[32]; // 256 bits for AES-256
            RandomNumberGenerator.Fill(newKey);

            // Create new version identifier
            var newVersion = $"v{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

            // Encrypt and save new key
            var encryptedKey = EncryptKeyFile(newKey);
            var keyFilePath = Path.Combine(
                _keyStorePath,
                string.Format(KeyFilePattern, newVersion));

            await File.WriteAllBytesAsync(keyFilePath, encryptedKey, cancellationToken);

            // Update current version pointer
            var versionFilePath = Path.Combine(_keyStorePath, CurrentVersionFile);
            await File.WriteAllTextAsync(versionFilePath, newVersion, cancellationToken);

            // Clear cache to force reload
            _keyCache.Clear();

            _logger.LogInformation("Successfully rotated encryption keys to version {Version}",
                newVersion);

            return newVersion;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rotate encryption keys");
            throw new KeyProviderException("Failed to rotate encryption keys", ex);
        }
    }

    private byte[] LoadAndCacheKey(byte[] encryptedKeyData, string cacheKey, string fieldName, string keyVersion)
    {
        var keyData = DecryptKeyFile(encryptedKeyData);
        var derivedKey = DeriveFieldKey(keyData, fieldName);
        CryptographicOperations.ZeroMemory(keyData);

        _keyCache.TryAdd(cacheKey, derivedKey);

        _logger.LogDebug("Loaded encryption key for field {FieldName} version {KeyVersion}",
            fieldName, keyVersion);

        return derivedKey;
    }

    /// <summary>
    /// Initializes the first encryption key
    /// </summary>
    private async Task<string> InitializeFirstKeyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing first encryption key");
        return await RotateKeysAsync(cancellationToken);
    }

    /// <summary>
    /// Synchronous initialization of the first encryption key, avoiding sync-over-async
    /// </summary>
    private string InitializeFirstKeySync()
    {
        _logger.LogInformation("Initializing first encryption key (sync path)");

        var newKey = new byte[32];
        RandomNumberGenerator.Fill(newKey);

        var newVersion = $"v{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

        var encryptedKey = EncryptKeyFile(newKey);
        var keyFilePath = Path.Combine(
            _keyStorePath,
            string.Format(KeyFilePattern, newVersion));

        File.WriteAllBytes(keyFilePath, encryptedKey);

        var versionFilePath = Path.Combine(_keyStorePath, CurrentVersionFile);
        File.WriteAllText(versionFilePath, newVersion);

        _keyCache.Clear();

        _logger.LogInformation("Successfully rotated encryption keys to version {Version}",
            newVersion);

        return newVersion;
    }

    /// <summary>
    /// Encrypts a key for storage using the master key
    /// </summary>
    private byte[] EncryptKeyFile(byte[] keyData)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[keyData.Length];
        var tag = new byte[16];

        using var aesGcm = new AesGcm(_masterKey, 16);
        aesGcm.Encrypt(nonce, keyData, ciphertext, tag);

        // Combine nonce + tag + ciphertext
        var result = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length + tag.Length, ciphertext.Length);

        return result;
    }

    /// <summary>
    /// Decrypts a key file using the master key
    /// </summary>
    private byte[] DecryptKeyFile(byte[] encryptedData)
    {
        const int nonceSize = 12;
        const int tagSize = 16;

        // Extract components
        var nonce = new byte[nonceSize];
        var tag = new byte[tagSize];
        var ciphertext = new byte[encryptedData.Length - nonceSize - tagSize];

        Buffer.BlockCopy(encryptedData, 0, nonce, 0, nonceSize);
        Buffer.BlockCopy(encryptedData, nonceSize, tag, 0, tagSize);
        Buffer.BlockCopy(encryptedData, nonceSize + tagSize, ciphertext, 0, ciphertext.Length);

        // Decrypt
        var plaintext = new byte[ciphertext.Length];

        using var aesGcm = new AesGcm(_masterKey, 16);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Fixed application-specific salt to avoid the degenerate all-zero HKDF salt case.
    /// Deterministic across all instances — maintains the deterministic derivation property.
    /// </summary>
    private static readonly byte[] ApplicationSalt =
        SHA256.HashData("MillWorks.AuditCore.FieldKeyDerivation"u8);

    /// <summary>
    /// Derives a field-specific key from the master key using HKDF
    /// </summary>
    private static byte[] DeriveFieldKey(byte[] masterKey, string fieldName)
    {
        var info = Encoding.UTF8.GetBytes($"field:{fieldName}");
        var derivedKey = new byte[32]; // 256 bits

        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, ApplicationSalt, info);

        return derivedKey;
    }
}