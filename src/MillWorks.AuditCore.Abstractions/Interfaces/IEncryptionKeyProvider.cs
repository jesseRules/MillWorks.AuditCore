namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Provider interface for encryption key management
/// </summary>
public interface IEncryptionKeyProvider
{
    /// <summary>
    /// Gets the current encryption key
    /// </summary>
    /// <param name="fieldName">Name of the field (for field-specific keys)</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Encryption key bytes</returns>
    Task<byte[]> GetEncryptionKeyAsync(string fieldName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific version of the encryption key
    /// </summary>
    /// <param name="fieldName">Name of the field</param>
    /// <param name="keyVersion">Version identifier</param>
    /// <param name="cancellationToken"></param>
    /// <returns>Encryption key bytes</returns>
    Task<byte[]> GetEncryptionKeyAsync(string fieldName, string keyVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current key version identifier
    /// </summary>
    /// <returns>Current key version</returns>
    Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronously gets the current encryption key.
    /// Override in providers that support true synchronous key retrieval.
    /// </summary>
    byte[] GetEncryptionKey(string fieldName) =>
        GetEncryptionKeyAsync(fieldName).GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously gets a specific version of the encryption key.
    /// Override in providers that support true synchronous key retrieval.
    /// </summary>
    byte[] GetEncryptionKey(string fieldName, string keyVersion) =>
        GetEncryptionKeyAsync(fieldName, keyVersion).GetAwaiter().GetResult();

    /// <summary>
    /// Synchronously gets the current key version identifier.
    /// Override in providers that support true synchronous key retrieval.
    /// </summary>
    string GetCurrentKeyVersion() =>
        GetCurrentKeyVersionAsync().GetAwaiter().GetResult();

    /// <summary>
    /// Rotates encryption keys
    /// </summary>
    /// <returns>New key version identifier</returns>
    Task<string> RotateKeysAsync(CancellationToken cancellationToken = default);
}