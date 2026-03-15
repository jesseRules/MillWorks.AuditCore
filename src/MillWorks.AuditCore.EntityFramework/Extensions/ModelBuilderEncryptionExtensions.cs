using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Attributes;
using MillWorks.AuditCore.EntityFramework.Conversion;

namespace MillWorks.AuditCore.EntityFramework.Extensions;

/// <summary>
/// Extensions to apply field-level encryption via EF Core value converters.
/// Call <see cref="UseFieldEncryption"/> in <c>OnModelCreating</c> to automatically
/// encrypt/decrypt properties marked with <see cref="EncryptedFieldAttribute"/> or
/// <see cref="SensitiveDataAttribute"/> (with <c>AutoEncrypt = true</c>).
/// </summary>
public static class ModelBuilderEncryptionExtensions
{
    /// <summary>
    /// Scans the EF Core model for properties marked with <see cref="EncryptedFieldAttribute"/>
    /// or <see cref="SensitiveDataAttribute"/> and applies <see cref="EncryptedValueConverter"/>.
    /// Reflection runs once at model build time (cached by EF Core), not per save/load.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="encryptionService">
    /// The encryption service. Must be singleton-safe since the model is cached for the application lifetime.
    /// </param>
    public static ModelBuilder UseFieldEncryption(
        this ModelBuilder modelBuilder,
        IFieldEncryptionService encryptionService)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(encryptionService);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                if (property.PropertyInfo is null)
                    continue;

                var encryptedAttr = property.PropertyInfo.GetCustomAttribute<EncryptedFieldAttribute>();
                if (encryptedAttr is { EncryptInDatabase: true })
                {
                    ApplyEncryption(property, encryptionService, encryptedAttr.KeyName);
                    continue;
                }

                var sensitiveAttr = property.PropertyInfo.GetCustomAttribute<SensitiveDataAttribute>();
                if (sensitiveAttr is { AutoEncrypt: true })
                {
                    ApplyEncryption(property, encryptionService, keyName: null);
                }
            }
        }

        return modelBuilder;
    }

    /// <summary>
    /// Applies the <see cref="EncryptedValueConverter"/> to the specified property.
    /// </summary>
    /// <param name="property"></param>
    /// <param name="encryptionService"></param>
    /// <param name="keyName"></param>
    /// <exception cref="InvalidOperationException"></exception>
    private static void ApplyEncryption(
        IMutableProperty property,
        IFieldEncryptionService encryptionService,
        string? keyName)
    {
        if (property.ClrType != typeof(string))
        {
            throw new InvalidOperationException(
                $"Property '{property.DeclaringType.DisplayName()}.{property.Name}' " +
                $"is marked for encryption but is type '{property.ClrType.Name}', not string.");
        }

        var fieldIdentifier = keyName ?? property.Name;
        property.SetValueConverter(new EncryptedValueConverter(encryptionService, fieldIdentifier));
    }
}
