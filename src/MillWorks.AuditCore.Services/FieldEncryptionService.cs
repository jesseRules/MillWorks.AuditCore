using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Encryption;

/// <summary>
/// Implementation of field-level encryption using AES-256-GCM
/// </summary>
public sealed class FieldEncryptionService(
    IEncryptionKeyProvider keyProvider,
    ILogger<FieldEncryptionService> logger)
    : IFieldEncryptionService
{
    // AES-GCM parameters
    /// <summary>
    /// Nonce size in bytes
    /// </summary>
    private const int _nonceSize = 12; // 96 bits recommended for GCM

    /// <summary>
    /// Tag size in bytes
    /// </summary>
    private const int _tagSize = 16; // 128 bits authentication tag

    /// <summary>
    /// Encryption prefix to identify encrypted values
    /// </summary>
    private const string _encryptionPrefix = "ENC_V1:"; // Prefix to identify encrypted values

    /// <summary>
    /// Current payload schema version
    /// </summary>
    private const int _currentSchemaVersion = 1;

    /// <inheritdoc />
    public async Task<string> EncryptFieldAsync(string plainText, string fieldName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var keyVersion = await keyProvider.GetCurrentKeyVersionAsync(cancellationToken);
            return await EncryptFieldWithVersionAsync(plainText, fieldName, keyVersion, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to encrypt field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> EncryptFieldWithVersionAsync(
        string plainText,
        string fieldName,
        string keyVersion, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        byte[]? plainBytes = null;
        byte[]? cipherBytes = null;
        try
        {
            // Get encryption key for this field and version
            var key = await keyProvider.GetEncryptionKeyAsync(fieldName, keyVersion, cancellationToken);

            // Generate random nonce (IV)
            var nonce = new byte[_nonceSize];
            RandomNumberGenerator.Fill(nonce);

            // Convert plaintext to bytes
            plainBytes = Encoding.UTF8.GetBytes(plainText);

            // Encrypt using AES-GCM with AAD for metadata authentication
            cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[_tagSize];

            // Build AAD: version|keyVersion|fieldName to authenticate metadata
            var aad = BuildAad(_currentSchemaVersion, keyVersion, fieldName);

            using var aesGcm = new AesGcm(key, _tagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);

            // Create encrypted payload with metadata
            var payload = new EncryptedFieldPayload
            {
                Version = _currentSchemaVersion,
                KeyVersion = keyVersion,
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipherBytes),
                Tag = Convert.ToBase64String(tag),
                FieldName = fieldName,
                EncryptedAt = DateTimeOffset.UtcNow
            };

            // Serialize and encode
            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

            return _encryptionPrefix + Convert.ToBase64String(payloadBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName} with version {KeyVersion}",
                fieldName, keyVersion);
            throw new FieldEncryptionException(
                $"Failed to encrypt field {fieldName} with version {keyVersion}", ex);
        }
        finally
        {
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
            if (cipherBytes != null) CryptographicOperations.ZeroMemory(cipherBytes);
        }
    }

    /// <inheritdoc />
    public async Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(encryptedValue) || !IsEncrypted(encryptedValue))
            return encryptedValue;

        byte[]? cipherBytes = null;
        byte[]? plainBytes = null;
        try
        {
            // Remove prefix and decode
            var payloadBase64 = encryptedValue[_encryptionPrefix.Length..];
            var payloadBytes = Convert.FromBase64String(payloadBase64);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);

            // Deserialize payload
            var payload = JsonSerializer.Deserialize<EncryptedFieldPayload>(payloadJson)
                          ?? throw new FieldEncryptionException("Failed to deserialize encrypted payload");

            // Validate schema version before proceeding
            if (payload.Version != _currentSchemaVersion)
            {
                throw new FieldEncryptionException(
                    $"Unsupported encryption schema version {payload.Version}. Expected {_currentSchemaVersion}.");
            }

            // Validate that the payload's field name matches the expected field
            if (!string.Equals(payload.FieldName, fieldName, StringComparison.Ordinal))
            {
                throw new FieldEncryptionException(
                    $"Field name mismatch: expected '{fieldName}' but payload contains '{payload.FieldName}'");
            }

            // Get decryption key
            var key = await keyProvider.GetEncryptionKeyAsync(
                fieldName,
                payload.KeyVersion, cancellationToken);

            // Decode encrypted components
            var nonce = Convert.FromBase64String(payload.Nonce);
            cipherBytes = Convert.FromBase64String(payload.Ciphertext);
            var tag = Convert.FromBase64String(payload.Tag);

            // Build AAD from metadata for authenticated decryption
            var aad = BuildAad(payload.Version, payload.KeyVersion, payload.FieldName);

            // Decrypt using AES-GCM with AAD
            plainBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(key, _tagSize);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes, aad);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Decryption failed for field {FieldName} - possible tampering",
                fieldName);
            throw new FieldEncryptionException(
                $"Decryption failed for field {fieldName} - data may be tampered", ex);
        }
        catch (FieldEncryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to decrypt field {fieldName}", ex);
        }
        finally
        {
            if (cipherBytes != null) CryptographicOperations.ZeroMemory(cipherBytes);
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <inheritdoc />
    public string EncryptField(string plainText, string fieldName)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var keyVersion = keyProvider.GetCurrentKeyVersion();
            return EncryptFieldWithVersion(plainText, fieldName, keyVersion);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to encrypt field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public string DecryptField(string encryptedValue, string fieldName)
    {
        if (string.IsNullOrEmpty(encryptedValue) || !IsEncrypted(encryptedValue))
            return encryptedValue;

        byte[]? cipherBytes = null;
        byte[]? plainBytes = null;
        try
        {
            var payloadBase64 = encryptedValue[_encryptionPrefix.Length..];
            var payloadBytes = Convert.FromBase64String(payloadBase64);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);

            var payload = JsonSerializer.Deserialize<EncryptedFieldPayload>(payloadJson)
                          ?? throw new FieldEncryptionException("Failed to deserialize encrypted payload");

            // Validate schema version before proceeding
            if (payload.Version != _currentSchemaVersion)
            {
                throw new FieldEncryptionException(
                    $"Unsupported encryption schema version {payload.Version}. Expected {_currentSchemaVersion}.");
            }

            if (!string.Equals(payload.FieldName, fieldName, StringComparison.Ordinal))
            {
                throw new FieldEncryptionException(
                    $"Field name mismatch: expected '{fieldName}' but payload contains '{payload.FieldName}'");
            }

            var key = keyProvider.GetEncryptionKey(fieldName, payload.KeyVersion);

            var nonce = Convert.FromBase64String(payload.Nonce);
            cipherBytes = Convert.FromBase64String(payload.Ciphertext);
            var tag = Convert.FromBase64String(payload.Tag);

            // Build AAD from metadata for authenticated decryption
            var aad = BuildAad(payload.Version, payload.KeyVersion, payload.FieldName);

            plainBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(key, _tagSize);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes, aad);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Decryption failed for field {FieldName} - possible tampering", fieldName);
            throw new FieldEncryptionException(
                $"Decryption failed for field {fieldName} - data may be tampered", ex);
        }
        catch (FieldEncryptionException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to decrypt field {fieldName}", ex);
        }
        finally
        {
            if (cipherBytes != null) CryptographicOperations.ZeroMemory(cipherBytes);
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <inheritdoc />
    public bool IsEncrypted(string? value) => !string.IsNullOrEmpty(value) && value.StartsWith(_encryptionPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts the field with a specific key version, allowing for key rotation and multiple active keys.
    /// </summary>
    /// <param name="plainText"></param>
    /// <param name="fieldName"></param>
    /// <param name="keyVersion"></param>
    /// <returns></returns>
    /// <exception cref="FieldEncryptionException"></exception>
    private string EncryptFieldWithVersion(string plainText, string fieldName, string keyVersion)
    {
        byte[]? plainBytes = null;
        byte[]? cipherBytes = null;
        try
        {
            var key = keyProvider.GetEncryptionKey(fieldName, keyVersion);

            var nonce = new byte[_nonceSize];
            RandomNumberGenerator.Fill(nonce);

            plainBytes = Encoding.UTF8.GetBytes(plainText);

            cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[_tagSize];

            // Build AAD: version|keyVersion|fieldName to authenticate metadata
            var aad = BuildAad(_currentSchemaVersion, keyVersion, fieldName);

            using var aesGcm = new AesGcm(key, _tagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag, aad);

            var payload = new EncryptedFieldPayload
            {
                Version = _currentSchemaVersion,
                KeyVersion = keyVersion,
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipherBytes),
                Tag = Convert.ToBase64String(tag),
                FieldName = fieldName,
                EncryptedAt = DateTimeOffset.UtcNow
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

            return _encryptionPrefix + Convert.ToBase64String(payloadBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName} with version {KeyVersion}",
                fieldName, keyVersion);
            throw new FieldEncryptionException(
                $"Failed to encrypt field {fieldName} with version {keyVersion}", ex);
        }
        finally
        {
            if (plainBytes != null) CryptographicOperations.ZeroMemory(plainBytes);
            if (cipherBytes != null) CryptographicOperations.ZeroMemory(cipherBytes);
        }
    }

    /// <inheritdoc />
    public async Task<string> ReEncryptFieldAsync(
        string encryptedValue,
        string fieldName,
        string newKeyVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            // Decrypt with old key
            var plainText = await DecryptFieldAsync(encryptedValue, fieldName, cancellationToken);

            // Re-encrypt with new key
            return await EncryptFieldWithVersionAsync(plainText, fieldName, newKeyVersion, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to re-encrypt field {FieldName} to version {NewVersion}",
                fieldName, newKeyVersion);
            throw new FieldEncryptionException(
                $"Failed to re-encrypt field {fieldName} to version {newKeyVersion}", ex);
        }
    }

    /// <summary>
    /// Builds Additional Authenticated Data (AAD) from metadata fields.
    /// AAD is included in GCM authentication but not encrypted, ensuring
    /// metadata integrity without storing it redundantly in ciphertext.
    /// Format: version|keyVersion|fieldName (pipe-delimited, UTF-8 encoded)
    /// </summary>
    private static byte[] BuildAad(int version, string keyVersion, string fieldName)
    {
        var aadString = $"{version}|{keyVersion}|{fieldName}";
        return Encoding.UTF8.GetBytes(aadString);
    }
}
