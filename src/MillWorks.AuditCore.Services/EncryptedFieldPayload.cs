namespace MillWorks.AuditCore.Services.Encryption;

/// <summary>
/// Encrypted field payload structure
/// </summary>
internal sealed class EncryptedFieldPayload
{
    /// <summary>
    /// Version of the encryption schema
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Key version used for encryption
    /// </summary>
    public string KeyVersion { get; set; } = string.Empty;

    /// <summary>
    /// Nonce (IV) used for encryption
    /// </summary>
    public string Nonce { get; set; } = string.Empty;

    /// <summary>
    /// Ciphertext of the encrypted data
    /// </summary>
    public string Ciphertext { get; set; } = string.Empty;

    /// <summary>
    /// Tag for authentication
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Field name associated with this encrypted value
    /// </summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>
    /// Encryption timestamp
    /// </summary>
    public DateTimeOffset EncryptedAt { get; set; }
}