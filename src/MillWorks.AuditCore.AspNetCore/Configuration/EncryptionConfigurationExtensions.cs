using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.AspNetCore.Configuration;

/// <summary>
/// Extension methods for configuring field-level encryption.
/// Encryption is applied via EF Core value converters — properties marked with
/// <see cref="EntityFramework.Attributes.EncryptedFieldAttribute"/> or
/// <see cref="SensitiveDataAttribute"/> (with AutoEncrypt)
/// are automatically encrypted at the database layer.
/// </summary>
public static class EncryptionConfigurationExtensions
{
    extension(MillWorksAuditBuilder builder)
    {
        /// <summary>
        /// Enables field-level encryption with Azure Key Vault
        /// </summary>
        public MillWorksAuditBuilder UseFieldEncryption(string keyVaultUrl)
        {
            builder.Services.AddSingleton<IEncryptionKeyProvider>(sp =>
                new AzureKeyVaultProvider(
                    keyVaultUrl,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AzureKeyVaultProvider>>()));

            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }

        /// <summary>
        /// Enables field-level encryption with file-based key storage (for DMZ/air-gapped)
        /// </summary>
        /// <param name="keyStorePath">Directory path for key storage</param>
        /// <param name="masterKeyBase64">Base64-encoded 256-bit master key</param>
        /// <param name="allowAutoKeyGeneration">
        /// When true, automatically generates initial encryption keys if none exist.
        /// Default is false (fail-safe) — missing keys throw KeyProviderException.
        /// Enable only for dev/bootstrap scenarios.
        /// </param>
        public MillWorksAuditBuilder UseFieldEncryptionWithFileStorage(
            string keyStorePath,
            string masterKeyBase64,
            bool allowAutoKeyGeneration = false)
        {
            builder.Services.AddSingleton<IEncryptionKeyProvider>(sp =>
                new FileBasedKeyProvider(
                    keyStorePath,
                    masterKeyBase64,
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileBasedKeyProvider>>(),
                    allowAutoKeyGeneration));

            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }

        /// <summary>
        /// Enables field-level encryption with a custom key provider
        /// </summary>
        public MillWorksAuditBuilder UseFieldEncryption(IEncryptionKeyProvider keyProvider)
        {
            builder.Services.AddSingleton(keyProvider);
            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }
    }
}
