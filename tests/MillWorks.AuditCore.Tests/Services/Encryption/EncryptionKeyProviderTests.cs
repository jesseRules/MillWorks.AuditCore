using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Tests for FileBasedKeyProvider (IEncryptionKeyProvider implementation)
/// </summary>
[TestFixture]
[Category("Unit")]
public class EncryptionKeyProviderTests
{
    private Mock<ILogger<FileBasedKeyProvider>> _mockLogger;
    private string _keyStorePath;
    private string _masterKeyBase64;
    private FileBasedKeyProvider _provider;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<FileBasedKeyProvider>>();

        // Create a temp directory for key storage
        _keyStorePath = Path.Combine(Path.GetTempPath(), $"audit-key-tests-{Guid.NewGuid()}");

        // Generate a valid 256-bit master key
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        _masterKeyBase64 = Convert.ToBase64String(masterKey);

        _provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object, allowAutoKeyGeneration: true);
    }

    [TearDown]
    public void TearDown()
    {
        // Clean up the temp directory
        if (Directory.Exists(_keyStorePath))
        {
            Directory.Delete(_keyStorePath, recursive: true);
        }
    }

    /// <summary>
    /// GetEncryptionKeyAsync returns a non-null, non-empty key
    /// </summary>
    [Test]
    public async Task GetEncryptionKeyAsync_ReturnsKey()
    {
        // Act
        byte[] key = await _provider.GetEncryptionKeyAsync("TestField");

        // Assert
        Assert.That(key, Is.Not.Null);
        Assert.That(key.Length, Is.GreaterThan(0));
        Assert.That(key.Length, Is.EqualTo(32)); // 256-bit derived key
    }

    /// <summary>
    /// GetCurrentKeyVersionAsync returns a non-null, non-empty version string
    /// </summary>
    [Test]
    public async Task GetCurrentKeyVersionAsync_ReturnsVersion()
    {
        // Act
        string version = await _provider.GetCurrentKeyVersionAsync();

        // Assert
        Assert.That(version, Is.Not.Null);
        Assert.That(version, Is.Not.Empty);
        Assert.That(version, Does.StartWith("v"));
    }

    /// <summary>
    /// RotateKeysAsync generates a new key with a different version
    /// </summary>
    [Test]
    public async Task RotateKeysAsync_GeneratesNewKey()
    {
        // Arrange - get the initial key and version
        string initialVersion = await _provider.GetCurrentKeyVersionAsync();
        byte[] initialKey = await _provider.GetEncryptionKeyAsync("TestField");

        // Small delay to ensure the version timestamp differs
        await Task.Delay(TimeSpan.FromMilliseconds(1100));

        // Act
        string newVersion = await _provider.RotateKeysAsync();

        // Assert - new version should differ from the initial one
        Assert.That(newVersion, Is.Not.Null);
        Assert.That(newVersion, Is.Not.Empty);
        Assert.That(newVersion, Is.Not.EqualTo(initialVersion));

        // The new key for the same field should be different after rotation
        byte[] newKey = await _provider.GetEncryptionKeyAsync("TestField");
        Assert.That(newKey, Is.Not.EqualTo(initialKey));
    }

    /// <summary>
    /// FileBasedKeyProvider persists keys to the file system
    /// </summary>
    [Test]
    public async Task FileBasedKeyProvider_PersistsToFile()
    {
        // Act - get a key, which will trigger key generation and file writes
        await _provider.GetEncryptionKeyAsync("PersistField");

        // Assert - verify the key store directory has files
        string[] files = Directory.GetFiles(_keyStorePath);
        Assert.That(files.Length, Is.GreaterThanOrEqualTo(2)); // current-version.txt + at least one key file

        // Verify version file exists
        string versionFilePath = Path.Combine(_keyStorePath, "current-version.txt");
        Assert.That(File.Exists(versionFilePath), Is.True);

        // Verify version file content
        string versionContent = await File.ReadAllTextAsync(versionFilePath);
        Assert.That(versionContent, Is.Not.Empty);
        Assert.That(versionContent, Does.StartWith("v"));
    }

    /// <summary>
    /// FileBasedKeyProvider reads an existing key from disk on subsequent access
    /// </summary>
    [Test]
    public async Task FileBasedKeyProvider_ReadsExistingKey()
    {
        // Arrange - generate a key with the first provider instance
        byte[] originalKey = await _provider.GetEncryptionKeyAsync("ConsistentField");
        string version = await _provider.GetCurrentKeyVersionAsync();

        // Act - create a new provider instance pointing to the same key store
        var newProvider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object, allowAutoKeyGeneration: true);
        byte[] reloadedKey = await newProvider.GetEncryptionKeyAsync("ConsistentField", version);

        // Assert - key should be identical across provider instances
        Assert.That(reloadedKey, Is.EqualTo(originalKey));
    }
}
