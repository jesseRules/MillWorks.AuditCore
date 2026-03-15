using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.AspNetCore.Configuration;

/// <summary>
/// Extension methods for configuring field-level encryption.
/// Encryption is applied via EF Core value converters — properties marked with
/// <see cref="EntityFramework.Attributes.EncryptedFieldAttribute"/> or
/// <see cref="EntityFramework.Attributes.SensitiveDataAttribute"/> (with AutoEncrypt)
/// are automatically encrypted at the database layer.
/// </summary>
public static class EncryptionConfigurationExtensions
{
    /// <summary>
    /// Enables field-level encryption with Azure Key Vault
    /// </summary>
    public static MillWorksAuditBuilder UseFieldEncryption(
        this MillWorksAuditBuilder builder,
        string keyVaultUrl)
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
    public static MillWorksAuditBuilder UseFieldEncryptionWithFileStorage(
        this MillWorksAuditBuilder builder,
        string keyStorePath,
        string masterKeyBase64)
    {
        builder.Services.AddSingleton<IEncryptionKeyProvider>(sp =>
            new FileBasedKeyProvider(
                keyStorePath,
                masterKeyBase64,
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<FileBasedKeyProvider>>()));

        builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

        return builder;
    }

    /// <summary>
    /// Enables field-level encryption with a custom key provider
    /// </summary>
    public static MillWorksAuditBuilder UseFieldEncryption(
        this MillWorksAuditBuilder builder,
        IEncryptionKeyProvider keyProvider)
    {
        builder.Services.AddSingleton(keyProvider);
        builder.Services.AddSingleton<IFieldEncryptionService, FieldEncryptionService>();

        return builder;
    }
}
