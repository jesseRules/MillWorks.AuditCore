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
    /// Fixed application-specific salt to avoid the degenerate all-zero HKDF salt case.
    /// Deterministic across all instances — maintains the deterministic derivation property.
    /// </summary>
    private static readonly byte[] _applicationSalt =
        SHA256.HashData("MillWorks.AuditCore.FieldKeyDerivation"u8);

    /// <summary>
    /// Derives a field-specific encryption key from a master key.
    /// Uses a distinct domain label to avoid collisions with the versioned overload.
    /// </summary>
    /// <param name="masterKey">Master encryption key</param>
    /// <param name="fieldName">Name of the field (e.g., "SSN", "Email")</param>
    /// <returns>32-byte derived key for the specific field</returns>
    public static byte[] DeriveFieldKey(byte[] masterKey, string fieldName)
    {
        // Domain label "field-unversioned:" is distinct from "field-versioned:" to prevent
        // collisions: field "X:version:1" (unversioned) vs field "X" version "1" (versioned)
        // would previously derive the same key. Length-prefixing the field name adds defense
        // in depth against embedded delimiters in the field name.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("field-unversioned:"u8);
        var fieldBytes = Encoding.UTF8.GetBytes(fieldName);
        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, fieldBytes.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(fieldBytes);
        var info = hash.GetHashAndReset();

        var derivedKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, _applicationSalt, info);
        return derivedKey;
    }

    /// <summary>
    /// Derives a field-specific key with version support.
    /// Uses a distinct domain label to avoid collisions with the unversioned overload.
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
        // Domain label "field-versioned:" is distinct from "field-unversioned:" to prevent
        // collisions. Length-prefixing both field name and version adds defense in depth
        // against embedded delimiters.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("field-versioned:"u8);

        var fieldBytes = Encoding.UTF8.GetBytes(fieldName);
        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, fieldBytes.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(fieldBytes);

        var versionBytes = Encoding.UTF8.GetBytes(keyVersion);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, versionBytes.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(versionBytes);

        var info = hash.GetHashAndReset();

        var derivedKey = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, derivedKey, _applicationSalt, info);
        return derivedKey;
    }
}
