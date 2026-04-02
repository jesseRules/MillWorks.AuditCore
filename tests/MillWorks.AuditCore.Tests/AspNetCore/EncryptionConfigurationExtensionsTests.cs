using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.AspNetCore.Configuration;
using MillWorks.AuditCore.AspNetCore.Configuration.Options;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.Tests.AspNetCore;

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
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IEncryptionKeyProvider)), Is.True);
        Assert.That(_services.Any(static s =>
            s.ServiceType == typeof(IFieldEncryptionService)
            && s.ImplementationType == typeof(FieldEncryptionService)), Is.True);

        using var provider = _services.BuildServiceProvider();
        var keyProvider = provider.GetRequiredService<IEncryptionKeyProvider>();
        Assert.That(keyProvider, Is.InstanceOf<AzureKeyVaultProvider>());
    }

    [Test]
    public void UseFieldEncryption_WithFileStorage_RegistersFileProviderAndEncryptionService()
    {
        var returned = _builder.UseFieldEncryptionWithFileStorage("/tmp/keys", "dGVzdGtleQ==");

        Assert.That(returned, Is.SameAs(_builder));
        Assert.That(_services.Any(static s => s.ServiceType == typeof(IEncryptionKeyProvider)), Is.True);

        using var provider = _services.BuildServiceProvider();
        var keyProvider = provider.GetRequiredService<IEncryptionKeyProvider>();
        var encryptionService = provider.GetRequiredService<IFieldEncryptionService>();

        Assert.That(keyProvider, Is.InstanceOf<FileBasedKeyProvider>());
        Assert.That(encryptionService, Is.InstanceOf<FieldEncryptionService>());
    }

    [Test]
    public void UseFieldEncryption_WithCustomProvider_RegistersExactInstance()
    {
        var keyProvider = new TestEncryptionKeyProvider();

        var returned = _builder.UseFieldEncryption(keyProvider);

        Assert.That(returned, Is.SameAs(_builder));

        using var provider = _services.BuildServiceProvider();
        Assert.That(provider.GetRequiredService<IEncryptionKeyProvider>(), Is.SameAs(keyProvider));
        Assert.That(provider.GetRequiredService<IFieldEncryptionService>(), Is.InstanceOf<FieldEncryptionService>());
    }

    private sealed class TestEncryptionKeyProvider : IEncryptionKeyProvider
    {
        public Task<byte[]> GetEncryptionKeyAsync(string fieldName, CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1, 2, 3, 4 });

        public Task<byte[]> GetEncryptionKeyAsync(
            string fieldName,
            string keyVersion,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new byte[] { 1, 2, 3, 4 });

        public Task<string> GetCurrentKeyVersionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("v1");

        public Task<string> RotateKeysAsync(CancellationToken cancellationToken = default)
            => Task.FromResult("v2");
    }
}
