using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.Cryptography;
using MillWorks.Cryptography.Aead;
using MillWorks.Cryptography.KeyManagement;

namespace MillWorks.AuditCore.Services.Encryption;

/// <summary>
/// Field-level encryption built on the shared <c>MillWorks.Cryptography</c> AEAD primitive.
/// The key material and HKDF field-derivation are owned by <see cref="IEncryptionKeyProvider"/>
/// (file-system or Key Vault backed); the AES-256-GCM cipher is <see cref="IAeadCipher"/>. This
/// service owns only the AuditCore storage envelope and the plaintext ⇄ string boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage envelope.</b> An encrypted column value is
/// <c>"ENC2:" + Base64( [envelopeVersion:1][keyVersionLen:2 BE][keyVersion][AEAD frame] )</c>,
/// where the AEAD frame is the canonical <see cref="AeadFormat"/> frame
/// <c>[version:1][nonce:12][tag:16][ciphertext]</c>. The key version is carried <i>outside</i> the
/// frame because decryption must know which key version to resolve for rotation — the frame itself
/// carries no key id. The <c>"ENC2:"</c> sentinel lets <see cref="IsEncrypted"/> distinguish an
/// already-encrypted value (so the EF value converter never double-encrypts).
/// </para>
/// <para>
/// <b>Context binding.</b> The tenant scope, key version, and field name are bound into the AEAD
/// associated data via <see cref="AeadContext.ForKey"/>, so a frame can only be decrypted under the
/// exact <c>(scope, version, field)</c> it was produced for: a cross-field or cross-version swap
/// fails authentication (surfaced as a tamper error), which is stronger than the previous
/// string-compare field check.
/// </para>
/// <para>
/// <b>Key scope.</b> AuditCore field encryption uses a single <see cref="KeyScope.Global"/> key
/// ring. The encryption seam is the EF value converter, which is bound at model-build time and
/// carries no per-row tenant context. The key provider is tenant-capable for future use; this
/// consumer is deliberately global.
/// </para>
/// </remarks>
public sealed class FieldEncryptionService(
    IEncryptionKeyProvider keyProvider,
    IAeadCipher cipher,
    ILogger<FieldEncryptionService> logger)
    : IFieldEncryptionService
{
    /// <summary>Storage-envelope sentinel that marks an encrypted column value.</summary>
    private const string EncryptionPrefix = "ENC2:";

    /// <summary>Current AuditCore storage-envelope version (distinct from the AEAD frame version).</summary>
    private const byte EnvelopeVersion = 1;

    /// <summary>The single global key ring AuditCore field encryption resolves against.</summary>
    private static readonly KeyScope Scope = KeyScope.Global;

    /// <inheritdoc />
    public async Task<string> EncryptFieldAsync(string plainText, string fieldName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        try
        {
            var keyVersion = await keyProvider.GetCurrentVersionAsync(Scope, cancellationToken).ConfigureAwait(false);
            return await EncryptFieldWithVersionAsync(plainText, fieldName, keyVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FieldEncryptionException)
        {
            throw;
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
        string keyVersion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(plainText))
            return plainText;

        ArgumentException.ThrowIfNullOrEmpty(fieldName);
        ArgumentException.ThrowIfNullOrEmpty(keyVersion);

        byte[]? plainBytes = null;
        try
        {
            using var key = await keyProvider
                .GetEncryptionKeyAsync(fieldName, keyVersion, Scope, cancellationToken)
                .ConfigureAwait(false);

            plainBytes = Encoding.UTF8.GetBytes(plainText);
            var associatedData = AeadContext.ForKey(Scope, keyVersion, fieldName);

            // The cipher writes the canonical [version][nonce][tag][ciphertext] frame; the
            // ciphertext (not the plaintext) lands in the returned buffer.
            var frame = cipher.Encrypt(key.Span, plainBytes, associatedData);
            return WrapEnvelope(keyVersion, frame);
        }
        catch (FieldEncryptionException)
        {
            throw;
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
            if (plainBytes is not null)
                CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <inheritdoc />
    public async Task<string> DecryptFieldAsync(string encryptedValue, string fieldName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(encryptedValue) || !IsEncrypted(encryptedValue))
            return encryptedValue;

        ArgumentException.ThrowIfNullOrEmpty(fieldName);

        byte[]? plainBytes = null;
        try
        {
            var (keyVersion, frame) = UnwrapEnvelope(encryptedValue);

            using var key = await keyProvider
                .GetEncryptionKeyAsync(fieldName, keyVersion, Scope, cancellationToken)
                .ConfigureAwait(false);

            var associatedData = AeadContext.ForKey(Scope, keyVersion, fieldName);

            // Authentication fails (CryptographyException) if the key, field, version, or
            // ciphertext was tampered with — the AAD binds field+version+scope.
            plainBytes = cipher.Decrypt(key.Span, frame, associatedData);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (FieldEncryptionException)
        {
            throw;
        }
        catch (CryptographyException ex)
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
        finally
        {
            if (plainBytes is not null)
                CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    /// <inheritdoc />
    public bool IsEncrypted(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(EncryptionPrefix, StringComparison.Ordinal);

    /// <inheritdoc />
    public async Task<string> ReEncryptFieldAsync(
        string encryptedValue,
        string fieldName,
        string newKeyVersion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var plainText = await DecryptFieldAsync(encryptedValue, fieldName, cancellationToken)
                .ConfigureAwait(false);
            return await EncryptFieldWithVersionAsync(plainText, fieldName, newKeyVersion, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A decrypt/encrypt sub-failure (itself a FieldEncryptionException) is intentionally wrapped
            // with the re-encryption context — the inner exception preserves the underlying cause.
            logger.LogError(ex, "Failed to re-encrypt field {FieldName} to version {NewVersion}",
                fieldName, newKeyVersion);
            throw new FieldEncryptionException(
                $"Failed to re-encrypt field {fieldName} to version {newKeyVersion}", ex);
        }
    }

    /// <summary>
    /// Builds the AuditCore storage envelope around a canonical AEAD frame, carrying the key
    /// version (length-prefixed, so it is robust against any delimiter in the version string).
    /// </summary>
    private static string WrapEnvelope(string keyVersion, byte[] frame)
    {
        var keyVersionBytes = Encoding.UTF8.GetBytes(keyVersion);
        if (keyVersionBytes.Length > ushort.MaxValue)
            throw new FieldEncryptionException("Key version is too long to encode in the storage envelope.");

        var envelope = new byte[sizeof(byte) + sizeof(ushort) + keyVersionBytes.Length + frame.Length];
        envelope[0] = EnvelopeVersion;
        BinaryPrimitives.WriteUInt16BigEndian(envelope.AsSpan(sizeof(byte), sizeof(ushort)), (ushort)keyVersionBytes.Length);
        keyVersionBytes.CopyTo(envelope.AsSpan(sizeof(byte) + sizeof(ushort)));
        frame.CopyTo(envelope.AsSpan(sizeof(byte) + sizeof(ushort) + keyVersionBytes.Length));

        return EncryptionPrefix + Convert.ToBase64String(envelope);
    }

    /// <summary>
    /// Parses the AuditCore storage envelope, returning the carried key version and the inner AEAD
    /// frame. Malformed input surfaces as <see cref="FieldEncryptionException"/>.
    /// </summary>
    private static (string KeyVersion, byte[] Frame) UnwrapEnvelope(string encryptedValue)
    {
        byte[] envelope;
        try
        {
            envelope = Convert.FromBase64String(encryptedValue[EncryptionPrefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new FieldEncryptionException("Encrypted value is not valid Base64.", ex);
        }

        const int headerSize = sizeof(byte) + sizeof(ushort);
        if (envelope.Length < headerSize)
            throw new FieldEncryptionException("Encrypted value envelope is truncated.");

        if (envelope[0] != EnvelopeVersion)
        {
            throw new FieldEncryptionException(
                $"Unsupported encryption envelope version {envelope[0]}. Expected {EnvelopeVersion}.");
        }

        var keyVersionLength = BinaryPrimitives.ReadUInt16BigEndian(envelope.AsSpan(sizeof(byte), sizeof(ushort)));
        if (envelope.Length < headerSize + keyVersionLength)
            throw new FieldEncryptionException("Encrypted value envelope is truncated.");

        var keyVersion = Encoding.UTF8.GetString(envelope, headerSize, keyVersionLength);
        var frame = envelope[(headerSize + keyVersionLength)..];
        return (keyVersion, frame);
    }
}
