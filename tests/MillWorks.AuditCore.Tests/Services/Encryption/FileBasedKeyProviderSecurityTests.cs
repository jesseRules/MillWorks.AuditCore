using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Phase 4: Security-focused tests for FileBasedKeyProvider.
/// Validates key storage safety, error handling, path validation, and thread safety.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public class FileBasedKeyProviderSecurityTests
{
    private Mock<ILogger<FileBasedKeyProvider>> _mockLogger;
    private string _keyStorePath;
    private string _masterKeyBase64;

    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<FileBasedKeyProvider>>();
        _keyStorePath = Path.Combine(Path.GetTempPath(), $"audit-key-sec-{Guid.NewGuid()}");
        var masterKey = new byte[32];
        RandomNumberGenerator.Fill(masterKey);
        _masterKeyBase64 = Convert.ToBase64String(masterKey);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_keyStorePath))
            Directory.Delete(_keyStorePath, recursive: true);
    }

    // ── Key file does not exist ──

    [Test]
    public void GetEncryptionKey_MissingKeyFile_ThrowsKeyProviderException()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        // Write a version file pointing to a version that has no key file
        File.WriteAllText(Path.Combine(_keyStorePath, "current-version.txt"), "v_nonexistent");

        var act = () => provider.GetEncryptionKey("TestField");
        act.Should().Throw<KeyProviderException>();
    }

    [Test]
    public async Task GetEncryptionKeyAsync_MissingKeyFile_ThrowsKeyProviderException()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        File.WriteAllText(Path.Combine(_keyStorePath, "current-version.txt"), "v_ghost");

        var act = () => provider.GetEncryptionKeyAsync("TestField", "v_ghost");
        await act.Should().ThrowAsync<KeyProviderException>();
    }

    // ── Empty key file ──

    [Test]
    public void GetEncryptionKey_EmptyKeyFile_ThrowsKeyProviderException()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var version = "v_empty";
        File.WriteAllText(Path.Combine(_keyStorePath, "current-version.txt"), version);
        File.WriteAllBytes(Path.Combine(_keyStorePath, $"key-{version}.encrypted"), []);

        var act = () => provider.GetEncryptionKey("TestField", version);
        act.Should().Throw<KeyProviderException>();
    }

    // ── Corrupted key file (bad encryption) ──

    [Test]
    public void GetEncryptionKey_CorruptedKeyFile_ThrowsKeyProviderException()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var version = "v_corrupt";
        File.WriteAllText(Path.Combine(_keyStorePath, "current-version.txt"), version);
        File.WriteAllBytes(Path.Combine(_keyStorePath, $"key-{version}.encrypted"),
            RandomNumberGenerator.GetBytes(64)); // Random garbage

        var act = () => provider.GetEncryptionKey("TestField", version);
        act.Should().Throw<KeyProviderException>();
    }

    // ── Key derivation produces different keys for different fields ──

    [Test]
    public async Task GetEncryptionKey_DifferentFields_DifferentDerivedKeys()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);

        var key1 = await provider.GetEncryptionKeyAsync("FieldA");
        var key2 = await provider.GetEncryptionKeyAsync("FieldB");

        key1.Should().NotBeEquivalentTo(key2, "HKDF should derive distinct keys per field name");
    }

    // ── Same field returns same derived key (caching) ──

    [Test]
    public async Task GetEncryptionKey_SameField_ReturnsCachedKey()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);

        var key1 = await provider.GetEncryptionKeyAsync("CachedField");
        var key2 = await provider.GetEncryptionKeyAsync("CachedField");

        key1.Should().BeEquivalentTo(key2);
        ReferenceEquals(key1, key2).Should().BeTrue("should return cached instance");
    }

    // ── Key rotation creates new version and new keys ──

    [Test]
    public async Task RotateKeysAsync_NewVersionAndNewKeys()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);

        var v1 = await provider.GetCurrentKeyVersionAsync();
        var keyV1 = await provider.GetEncryptionKeyAsync("TestField");

        await Task.Delay(1100); // Ensure version timestamp differs

        var v2 = await provider.RotateKeysAsync();
        var keyV2 = await provider.GetEncryptionKeyAsync("TestField");

        v2.Should().NotBe(v1);
        keyV2.Should().NotBeEquivalentTo(keyV1, "rotated key should produce different derived key");
    }

    // ── Old key version still accessible after rotation ──

    [Test]
    public async Task RotateKeysAsync_OldVersionStillAccessible()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);

        var v1 = await provider.GetCurrentKeyVersionAsync();
        var keyV1 = await provider.GetEncryptionKeyAsync("TestField", v1);

        await Task.Delay(1100);
        await provider.RotateKeysAsync();

        // Old key should still be loadable
        var reloadedV1 = await provider.GetEncryptionKeyAsync("TestField", v1);
        reloadedV1.Should().BeEquivalentTo(keyV1);
    }

    // ── Key is 32 bytes (256-bit) ──

    [Test]
    public async Task GetEncryptionKey_Returns32ByteKey()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var key = await provider.GetEncryptionKeyAsync("SizeCheck");

        key.Length.Should().Be(32, "AES-256 requires 32-byte keys");
    }

    // ── Concurrent reads are safe ──

    [Test]
    public async Task GetEncryptionKeyAsync_ConcurrentReads_AllSucceed()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        // Initialize a key first
        await provider.GetEncryptionKeyAsync("ConcurrentField");

        var tasks = Enumerable.Range(0, 20)
            .Select(_ => provider.GetEncryptionKeyAsync("ConcurrentField"))
            .ToList();

        var results = await Task.WhenAll(tasks);
        results.Should().AllSatisfy(k =>
        {
            k.Length.Should().Be(32);
            k.Should().BeEquivalentTo(results[0]);
        });
    }

    // ── New provider instance reads existing keys ──

    [Test]
    public async Task NewProviderInstance_ReadsExistingKeys()
    {
        var provider1 = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var version = await provider1.GetCurrentKeyVersionAsync();
        var key1 = await provider1.GetEncryptionKeyAsync("PersistField", version);

        // Create a completely new provider instance
        var provider2 = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var key2 = await provider2.GetEncryptionKeyAsync("PersistField", version);

        key2.Should().BeEquivalentTo(key1, "same master key + same version should yield same derived key");
    }

    // ── Wrong master key cannot decrypt key files ──

    [Test]
    public async Task WrongMasterKey_CannotDecryptKeyFiles()
    {
        var provider1 = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var version = await provider1.GetCurrentKeyVersionAsync();

        // Create provider with different master key
        var wrongMaster = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider2 = new FileBasedKeyProvider(_keyStorePath, wrongMaster, _mockLogger.Object);

        var act = () => provider2.GetEncryptionKeyAsync("TestField", version);
        await act.Should().ThrowAsync<KeyProviderException>();
    }

    // ── Sync path ──

    [Test]
    public void GetCurrentKeyVersion_Sync_ReturnsValidVersion()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var version = provider.GetCurrentKeyVersion();

        version.Should().NotBeNullOrEmpty();
        version.Should().StartWith("v");
    }

    [Test]
    public void GetEncryptionKey_Sync_ReturnsValidKey()
    {
        var provider = new FileBasedKeyProvider(_keyStorePath, _masterKeyBase64, _mockLogger.Object);
        var key = provider.GetEncryptionKey("SyncField");

        key.Length.Should().Be(32);
    }
}
