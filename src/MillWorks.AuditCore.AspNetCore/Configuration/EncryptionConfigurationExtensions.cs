using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MillWorks.AuditCore.Abstractions.Attributes;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.Cryptography.Aead;
using MillWorks.Cryptography.FileSystem;
using MillWorks.Cryptography.KeyManagement;
using MillWorks.Cryptography.KeyVault;
using MillWorks.Cryptography.Random;

namespace MillWorks.AuditCore.AspNetCore.Configuration;

/// <summary>
/// Extension methods for configuring field-level encryption.
/// Encryption is applied via EF Core value converters — properties marked with
/// <see cref="EntityFramework.Attributes.EncryptedFieldAttribute"/> or
/// <see cref="SensitiveDataAttribute"/> (with AutoEncrypt)
/// are automatically encrypted at the database layer.
/// </summary>
/// <remarks>
/// The encryption key material and HKDF field-derivation are owned by
/// <see cref="MillWorks.Cryptography"/>'s <see cref="IEncryptionKeyProvider"/> (file-system or Key
/// Vault backed); the AES-256-GCM cipher is its <see cref="IAeadCipher"/>. These are wired here and
/// consumed by <see cref="FieldEncryptionService"/>. The encryption key space is disjoint from the
/// integrity signing-key space wired by <c>UseSecurity</c> — different providers, different stores.
/// Each <c>UseFieldEncryption*</c> overload registers the provider with <c>TryAdd</c>, so a host can
/// register its own <see cref="IEncryptionKeyProvider"/> before <c>AddMillWorksAudit</c> to override it.
/// </remarks>
public static class EncryptionConfigurationExtensions
{
    extension(MillWorksAuditBuilder builder)
    {
        /// <summary>
        /// Enables field-level encryption with an Azure Key Vault backed key provider.
        /// </summary>
        /// <param name="keyVaultUrl">Absolute URI of the Key Vault.</param>
        public MillWorksAuditBuilder UseFieldEncryption(string keyVaultUrl)
        {
            if (string.IsNullOrWhiteSpace(keyVaultUrl) ||
                !Uri.TryCreate(keyVaultUrl, UriKind.Absolute, out var vaultUri))
            {
                throw new ArgumentException("A valid absolute Key Vault URL is required.", nameof(keyVaultUrl));
            }

            builder.Services.AddMillWorksCryptography();
            builder.Services.TryAddSingleton<IEncryptionKeyProvider>(sp => new AzureKeyVaultEncryptionKeyProvider(
                new SecretClient(vaultUri, new DefaultAzureCredential()),
                sp.GetRequiredService<ISecureRandom>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                cacheTtl: TimeSpan.FromHours(1)));
            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }

        /// <summary>
        /// Enables field-level encryption with a file-based key provider (for DMZ/air-gapped).
        /// </summary>
        /// <param name="keyStorePath">Directory path for key storage.</param>
        /// <param name="masterKeyBase64">Base64-encoded 256-bit master key that wraps stored keys at rest.</param>
        /// <param name="allowAutoKeyGeneration">
        /// When true, an initial encryption key is generated on first use if none exists.
        /// Default is false (fail-closed) — missing keys throw. Enable only for dev/bootstrap scenarios.
        /// </param>
        public MillWorksAuditBuilder UseFieldEncryptionWithFileStorage(
            string keyStorePath,
            string masterKeyBase64,
            bool allowAutoKeyGeneration = false)
        {
            var options = new FileSystemKeyProviderOptions
            {
                KeyStorePath = keyStorePath,
                MasterKeyBase64 = masterKeyBase64,
                AllowAutoKeyGeneration = allowAutoKeyGeneration,
            };

            builder.Services.AddMillWorksCryptography();
            builder.Services.TryAddSingleton<IEncryptionKeyProvider>(sp => new FileEncryptionKeyProvider(
                sp.GetRequiredService<IAeadCipher>(),
                sp.GetRequiredService<ISecureRandom>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                options));
            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }

        /// <summary>
        /// Enables field-level encryption with a custom <see cref="IEncryptionKeyProvider"/>.
        /// </summary>
        public MillWorksAuditBuilder UseFieldEncryption(IEncryptionKeyProvider keyProvider)
        {
            ArgumentNullException.ThrowIfNull(keyProvider);

            builder.Services.AddMillWorksCryptography();
            builder.Services.TryAddSingleton(keyProvider);
            builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

            return builder;
        }
    }
}
