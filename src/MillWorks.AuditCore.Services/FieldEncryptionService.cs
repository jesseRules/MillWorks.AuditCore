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
    private const int NonceSize = 12; // 96 bits recommended for GCM

    /// <summary>
    /// Tag size in bytes
    /// </summary>
    private const int TagSize = 16; // 128 bits authentication tag

    /// <summary>
    /// Encryption prefix to identify encrypted values
    /// </summary>
    private const string EncryptionPrefix = "ENC_V1:"; // Prefix to identify encrypted values

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

        try
        {
            // Get encryption key for this field and version
            var key = await keyProvider.GetEncryptionKeyAsync(fieldName, keyVersion, cancellationToken);

            // Generate random nonce (IV)
            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            // Convert plaintext to bytes
            var plainBytes = Encoding.UTF8.GetBytes(plainText);

            // Encrypt using AES-GCM
            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

            // Create encrypted payload with metadata
            var payload = new EncryptedFieldPayload
            {
                Version = 1,
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

            return EncryptionPrefix + Convert.ToBase64String(payloadBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName} with version {KeyVersion}",
                fieldName, keyVersion);
            throw new FieldEncryptionException(
                $"Failed to encrypt field {fieldName} with version {keyVersion}", ex);
        }
    }

    /// <inheritdoc />
    public async Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(encryptedValue) || !IsEncrypted(encryptedValue))
            return encryptedValue;

        try
        {
            // Remove prefix and decode
            var payloadBase64 = encryptedValue[EncryptionPrefix.Length..];
            var payloadBytes = Convert.FromBase64String(payloadBase64);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);

            // Deserialize payload
            var payload = JsonSerializer.Deserialize<EncryptedFieldPayload>(payloadJson)
                          ?? throw new FieldEncryptionException("Failed to deserialize encrypted payload");

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
            var cipherBytes = Convert.FromBase64String(payload.Ciphertext);
            var tag = Convert.FromBase64String(payload.Tag);

            // Decrypt using AES-GCM
            var plainBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Decryption failed for field {FieldName} - possible tampering",
                fieldName);
            throw new FieldEncryptionException(
                $"Decryption failed for field {fieldName} - data may be tampered", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to decrypt field {fieldName}", ex);
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

        try
        {
            var payloadBase64 = encryptedValue[EncryptionPrefix.Length..];
            var payloadBytes = Convert.FromBase64String(payloadBase64);
            var payloadJson = Encoding.UTF8.GetString(payloadBytes);

            var payload = JsonSerializer.Deserialize<EncryptedFieldPayload>(payloadJson)
                          ?? throw new FieldEncryptionException("Failed to deserialize encrypted payload");

            if (!string.Equals(payload.FieldName, fieldName, StringComparison.Ordinal))
            {
                throw new FieldEncryptionException(
                    $"Field name mismatch: expected '{fieldName}' but payload contains '{payload.FieldName}'");
            }

            var key = keyProvider.GetEncryptionKey(fieldName, payload.KeyVersion);

            var nonce = Convert.FromBase64String(payload.Nonce);
            var cipherBytes = Convert.FromBase64String(payload.Ciphertext);
            var tag = Convert.FromBase64String(payload.Tag);

            var plainBytes = new byte[cipherBytes.Length];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, cipherBytes, tag, plainBytes);

            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (CryptographicException ex)
        {
            logger.LogError(ex, "Decryption failed for field {FieldName} - possible tampering", fieldName);
            throw new FieldEncryptionException(
                $"Decryption failed for field {fieldName} - data may be tampered", ex);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to decrypt field {FieldName}", fieldName);
            throw new FieldEncryptionException($"Failed to decrypt field {fieldName}", ex);
        }
    }

    /// <inheritdoc />
    public bool IsEncrypted(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.StartsWith(EncryptionPrefix);
    }

    private string EncryptFieldWithVersion(string plainText, string fieldName, string keyVersion)
    {
        try
        {
            var key = keyProvider.GetEncryptionKey(fieldName, keyVersion);

            var nonce = new byte[NonceSize];
            RandomNumberGenerator.Fill(nonce);

            var plainBytes = Encoding.UTF8.GetBytes(plainText);

            var cipherBytes = new byte[plainBytes.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plainBytes, cipherBytes, tag);

            var payload = new EncryptedFieldPayload
            {
                Version = 1,
                KeyVersion = keyVersion,
                Nonce = Convert.ToBase64String(nonce),
                Ciphertext = Convert.ToBase64String(cipherBytes),
                Tag = Convert.ToBase64String(tag),
                FieldName = fieldName,
                EncryptedAt = DateTimeOffset.UtcNow
            };

            var payloadJson = JsonSerializer.Serialize(payload);
            var payloadBytes = Encoding.UTF8.GetBytes(payloadJson);

            return EncryptionPrefix + Convert.ToBase64String(payloadBytes);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to encrypt field {FieldName} with version {KeyVersion}",
                fieldName, keyVersion);
            throw new FieldEncryptionException(
                $"Failed to encrypt field {fieldName} with version {keyVersion}", ex);
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
}