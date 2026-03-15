using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Conversion;

/// <summary>
/// Value converter that transparently encrypts data to the database provider and decrypts from it.
/// The change tracker only ever sees plaintext, preventing dirty-context loops.
/// </summary>
public sealed class EncryptedValueConverter : ValueConverter<string, string>
{
    /// <summary>
    /// Creates a new encrypted value converter for the specified field.
    /// </summary>
    /// <param name="encryptionService">Encryption service (must be singleton-safe).</param>
    /// <param name="fieldName">Logical field name for key derivation.</param>
    /// <param name="mappingHints">Optional mapping hints for database column types.</param>
    public EncryptedValueConverter(
        IFieldEncryptionService encryptionService,
        string fieldName,
        ConverterMappingHints? mappingHints = null)
        : base(
            plaintext => Encrypt(encryptionService, plaintext, fieldName),
            ciphertext => Decrypt(encryptionService, ciphertext, fieldName),
            mappingHints)
    {
    }

    /// <summary>
    /// Encrypts the input string using the provided encryption service and field name.
    /// </summary>
    /// <param name="service"></param>
    /// <param name="input"></param>
    /// <param name="fieldName"></param>
    /// <returns></returns>
    private static string Encrypt(IFieldEncryptionService service, string input, string fieldName)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Idempotency: don't double-encrypt
        return service.IsEncrypted(input)
            ? input
            :
            // Let encryption failures propagate — saving plaintext violates security policy
            service.EncryptField(input, fieldName);
    }

    /// <summary>
    /// Decrypts the input string using the provided encryption service and field name.
    /// </summary>
    /// <param name="service"></param>
    /// <param name="input"></param>
    /// <param name="fieldName"></param>
    /// <returns></returns>
    private static string Decrypt(IFieldEncryptionService service, string input, string fieldName)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // Backward compatibility: plaintext legacy data passes through
        if (!service.IsEncrypted(input))
            return input;

        // Let decryption failures propagate — caller decides error handling policy
        return service.DecryptField(input, fieldName);
    }
}