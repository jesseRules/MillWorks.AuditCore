using System.Security.Cryptography;
using System.Text;

namespace MillWorks.AuditCore.Services.Encryption;

/// <summary>
/// Helper for deriving field-specific encryption keys from a master key using HKDF-SHA256.
/// Use this when implementing a custom <see cref="Abstractions.Interfaces.IEncryptionKeyProvider"/>
/// to ensure key derivation is consistent with the built-in providers.
/// </summary>
public static class FieldKeyDerivation
{
    /// <summary>
    /// Derives a field-specific encryption key from a master key
    /// </summary>
    /// <param name="masterKey">Master encryption key</param>
    /// <param name="fieldName">Name of the field (e.g., "SSN", "Email")</param>
    /// <returns>32-byte derived key for the specific field</returns>
    public static byte[] DeriveFieldKey(byte[] masterKey, string fieldName)
    {
        var info = Encoding.UTF8.GetBytes($"field:{fieldName}");
        var salt = new byte[32];
        var derivedKey = new byte[32];

        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, salt, info);

        return derivedKey;
    }

    /// <summary>
    /// Derives a field-specific key with version support
    /// </summary>
    /// <param name="masterKey">Master encryption key</param>
    /// <param name="fieldName">Name of the field (e.g., "SSN", "Email")</param>
    /// <param name="keyVersion">Key version identifier</param>
    /// <returns>32-byte derived key for the specific field and version</returns>
    public static byte[] DeriveFieldKey(
        byte[] masterKey,
        string fieldName,
        string keyVersion)
    {
        var info = Encoding.UTF8.GetBytes($"field:{fieldName}:version:{keyVersion}");
        var salt = new byte[32];
        var derivedKey = new byte[32];

        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, salt, info);

        return derivedKey;
    }
}