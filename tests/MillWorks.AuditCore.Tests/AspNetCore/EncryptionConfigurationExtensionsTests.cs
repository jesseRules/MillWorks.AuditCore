using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.Cryptography.FileSystem;
using MillWorks.Cryptography.KeyManagement;
using MillWorks.Cryptography.KeyVault;

namespace MillWorks.AuditCore.Tests.AspNetCore;

/// <summary>
/// Verifies that the field-encryption DI extensions wire MillWorks.Cryptography's encryption-key
/// providers (Cryptography consolidation A2) and the AuditCore <see cref="FieldEncryptionService"/>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class EncryptionConfigurationExtensionsTests
{
    private IServiceCollection _services = null!;
    private MillWorksAuditBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _services = new ServiceCollection();
        _services.AddLogging();
        _builder = new MillWorksAuditBuilder(_services, new AuditOptions { ApplicationName = "TestApp" });
    }

    [Test]
    public void UseFieldEncryption_WithKeyVault_RegistersAzureProviderAndEncryptionService()
    {
        var returned = _builder.UseFieldEncryption("https://vault.example");

        Assert.That(returned, Is.SameAs(_builder));
        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(IFieldEncryptionService)
            && s.ImplementationType == typeof(FieldEncryptionService)), Is.True);

        using var provider = _services.BuildServiceProvider();
        var keyProvider = provider.GetRequiredService<IEncryptionKeyProvider>();
        Assert.That(keyProvider, Is.InstanceOf<AzureKeyVaultEncryptionKeyProvider>());
        Assert.That(provider.GetRequiredService<IFieldEncryptionService>(), Is.InstanceOf<FieldEncryptionService>());
    }

    [Test]
    public void UseFieldEncryption_WithInvalidKeyVaultUrl_Throws()
    {
        Assert.Throws<ArgumentException>(() => _builder.UseFieldEncryption("not-a-url"));
    }

    [Test]
    public void UseFieldEncryption_WithFileStorage_RegistersFileProviderAndEncryptionService()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid()}");
        try
        {
            var returned = _builder.UseFieldEncryptionWithFileStorage(
                keyPath, GenerateValidMasterKey(), allowAutoKeyGeneration: true);

            Assert.That(returned, Is.SameAs(_builder));

            using var provider = _services.BuildServiceProvider();
            var keyProvider = provider.GetRequiredService<IEncryptionKeyProvider>();
            var encryptionService = provider.GetRequiredService<IFieldEncryptionService>();

            Assert.That(keyProvider, Is.InstanceOf<FileEncryptionKeyProvider>());
            Assert.That(encryptionService, Is.InstanceOf<FieldEncryptionService>());
        }
        finally
        {
            if (Directory.Exists(keyPath))
                Directory.Delete(keyPath, recursive: true);
        }
    }

    [Test]
    public void UseFieldEncryption_WithFileStorage_AutoGenDisabled_ThrowsWhenNoKeys()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"keys-{Guid.NewGuid()}");
        try
        {
            _builder.UseFieldEncryptionWithFileStorage(
                keyPath, GenerateValidMasterKey(), allowAutoKeyGeneration: false);

            using var provider = _services.BuildServiceProvider();
            var keyProvider = provider.GetRequiredService<IEncryptionKeyProvider>();

            Assert.ThrowsAsync<KeyProviderException>(() => keyProvider.GetCurrentVersionAsync(KeyScope.Global));
        }
        finally
        {
            if (Directory.Exists(keyPath))
                Directory.Delete(keyPath, recursive: true);
        }
    }

    [Test]
    public void UseFieldEncryption_WithCustomProvider_RegistersExactInstance()
    {
        var keyProvider = new FakeEncryptionKeyProvider();

        var returned = _builder.UseFieldEncryption(keyProvider);

        Assert.That(returned, Is.SameAs(_builder));

        using var provider = _services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<IEncryptionKeyProvider>(), Is.SameAs(keyProvider));
        Assert.That(provider.GetRequiredService<IFieldEncryptionService>(), Is.InstanceOf<FieldEncryptionService>());
    }

    private static string GenerateValidMasterKey()
    {
        var key = new byte[32];
        RandomNumberGenerator.Fill(key);
        return Convert.ToBase64String(key);
    }
}
