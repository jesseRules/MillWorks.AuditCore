namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Service for encrypting and decrypting individual fields in audit data
/// </summary>
public interface IFieldEncryptionService
{
    /// <summary>
    /// Encrypts a single field value
    /// </summary>
    /// <param name="plainText">Plain text value to encrypt</param>
    /// <param name="fieldName">Name of the field (used for key derivation)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Encrypted value as base64 string</returns>
    Task<string> EncryptFieldAsync(string plainText, string fieldName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrypts a single field value
    /// </summary>
    /// <param name="encryptedValue">Encrypted value as base64 string</param>
    /// <param name="fieldName">Name of the field (used for key derivation)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Decrypted plain text value</returns>
    Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Encrypts a field value with a specific key version
    /// </summary>
    /// <param name="plainText">Plain text value to encrypt</param>
    /// <param name="fieldName">Name of the field</param>
    /// <param name="keyVersion">Specific key version to use</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Encrypted value as base64 string with key version metadata</returns>
    Task<string> EncryptFieldWithVersionAsync(string plainText, string fieldName, string keyVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously encrypts a single field value.
    /// Uses synchronous key retrieval — override in providers for true sync support.
    /// </summary>
    string EncryptField(string plainText, string fieldName) =>
        EncryptFieldAsync(plainText, fieldName).GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously decrypts a single field value.
    /// Uses synchronous key retrieval — override in providers for true sync support.
    /// </summary>
    string DecryptField(string encryptedValue, string fieldName) =>
        DecryptFieldAsync(encryptedValue, fieldName).GetAwaiter().GetResult();

    /// <summary>
    /// Checks if a field value is encrypted
    /// </summary>
    /// <param name="value">Value to check</param>
    /// <returns>True if the value appears to be encrypted</returns>
    bool IsEncrypted(string? value);

    /// <summary>
    /// Re-encrypts a field with a new key (for key rotation)
    /// </summary>
    /// <param name="encryptedValue">Currently encrypted value</param>
    /// <param name="fieldName">Name of the field</param>
    /// <param name="newKeyVersion">New key version to use</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Re-encrypted value with new key</returns>
    Task<string> ReEncryptFieldAsync(string encryptedValue, string fieldName, string newKeyVersion,
        CancellationToken cancellationToken = default);
}