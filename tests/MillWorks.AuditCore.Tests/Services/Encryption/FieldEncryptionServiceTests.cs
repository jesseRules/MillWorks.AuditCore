using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

[TestFixture]
[Category("Unit")]
public class FieldEncryptionServiceTests
{
    private Mock<IEncryptionKeyProvider> _mockKeyProvider;
    private Mock<ILogger<FieldEncryptionService>> _mockLogger;
    private FieldEncryptionService _service;

    // A valid 256-bit key for AES-256-GCM
    private static readonly byte[] TestKey = new byte[32]
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    private static readonly byte[] AlternateKey = new byte[32]
    {
        0x20, 0x1F, 0x1E, 0x1D, 0x1C, 0x1B, 0x1A, 0x19,
        0x18, 0x17, 0x16, 0x15, 0x14, 0x13, 0x12, 0x11,
        0x10, 0x0F, 0x0E, 0x0D, 0x0C, 0x0B, 0x0A, 0x09,
        0x08, 0x07, 0x06, 0x05, 0x04, 0x03, 0x02, 0x01
    };

    [SetUp]
    public void Setup()
    {
        _mockKeyProvider = new Mock<IEncryptionKeyProvider>();
        _mockLogger = new Mock<ILogger<FieldEncryptionService>>();

        _mockKeyProvider.Setup(static kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("v1");
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKeyAsync("TestField", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKeyAsync("TestField", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlternateKey);

        // Sync overloads
        _mockKeyProvider.Setup(static kp => kp.GetCurrentKeyVersion()).Returns("v1");
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKey("TestField", "v1")).Returns(TestKey);
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKey("TestField", "v2")).Returns(AlternateKey);

        _service = new FieldEncryptionService(_mockKeyProvider.Object, _mockLogger.Object);
    }

    [Test]
    public async Task EncryptFieldAsync_PlainText_ReturnsEncryptedPayloadWithPrefix()
    {
        var encrypted = await _service.EncryptFieldAsync("Hello, World!", "TestField");

        Assert.That(encrypted, Does.StartWith("ENC_V1:"));
        Assert.That(encrypted, Is.Not.EqualTo("Hello, World!"));
    }

    [Test]
    public async Task EncryptAndDecrypt_RoundTrip_ReturnsOriginalText()
    {
        var original = "Sensitive data that needs encryption";

        var encrypted = await _service.EncryptFieldAsync(original, "TestField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "TestField");

        Assert.That(decrypted, Is.EqualTo(original));
    }

    [Test]
    public async Task EncryptFieldAsync_EmptyString_ReturnsEmptyString()
    {
        var result = await _service.EncryptFieldAsync("", "TestField");

        Assert.That(result, Is.EqualTo(""));
    }

    [Test]
    public async Task EncryptFieldAsync_NullInput_ReturnsNull()
    {
        var result = await _service.EncryptFieldAsync(null!, "TestField");

        Assert.That(result, Is.Null);
    }

    [Test]
    public void IsEncrypted_EncryptedPayload_ReturnsTrue()
    {
        var encrypted = _service.EncryptField("test", "TestField");

        Assert.That(_service.IsEncrypted(encrypted), Is.True);
    }

    [Test]
    public void IsEncrypted_PlainText_ReturnsFalse()
    {
        Assert.That(_service.IsEncrypted("just plain text"), Is.False);
    }

    [Test]
    public void IsEncrypted_NullOrEmpty_ReturnsFalse()
    {
        Assert.That(_service.IsEncrypted(null), Is.False);
        Assert.That(_service.IsEncrypted(""), Is.False);
    }

    [Test]
    public async Task EncryptFieldWithVersionAsync_StoresKeyVersion()
    {
        var encrypted = await _service.EncryptFieldWithVersionAsync(
            "versioned data", "TestField", "v1");

        Assert.That(encrypted, Does.StartWith("ENC_V1:"));

        // Decrypt should work with the same version key
        var decrypted = await _service.DecryptFieldAsync(encrypted, "TestField");
        Assert.That(decrypted, Is.EqualTo("versioned data"));
    }

    [Test]
    public async Task ReEncryptFieldAsync_NewKey_ProducesNewCiphertext()
    {
        var original = "data to re-encrypt";
        var encryptedV1 = await _service.EncryptFieldAsync(original, "TestField");

        var encryptedV2 = await _service.ReEncryptFieldAsync(encryptedV1, "TestField", "v2");

        // The ciphertext should be different
        Assert.That(encryptedV2, Is.Not.EqualTo(encryptedV1));

        // But decrypting with v2 key should return original
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKeyAsync("TestField", "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlternateKey);
        var decrypted = await _service.DecryptFieldAsync(encryptedV2, "TestField");
        Assert.That(decrypted, Is.EqualTo(original));
    }

    [Test]
    public async Task DecryptFieldAsync_WrongKey_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Setup wrong key for decryption
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKeyAsync("TestField", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlternateKey);

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(encrypted, "TestField"));
    }

    [Test]
    public void DecryptFieldAsync_CorruptedPayload_ThrowsFieldEncryptionException()
    {
        var corruptedPayload = "ENC_V1:" + Convert.ToBase64String("not-valid-json"u8.ToArray());

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(corruptedPayload, "TestField"));
    }

    [Test]
    public async Task DecryptFieldAsync_PlainText_ReturnsAsIs()
    {
        var plainText = "not encrypted at all";

        var result = await _service.DecryptFieldAsync(plainText, "TestField");

        Assert.That(result, Is.EqualTo(plainText));
    }

    [Test]
    public async Task DecryptFieldAsync_FieldNameMismatch_ThrowsFieldEncryptionException()
    {
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKeyAsync("OtherField", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);

        var encrypted = await _service.EncryptFieldAsync("test", "TestField");

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(encrypted, "OtherField"));
    }

    [Test]
    public void EncryptField_Sync_RoundTrips()
    {
        var original = "sync encryption test";

        var encrypted = _service.EncryptField(original, "TestField");
        var decrypted = _service.DecryptField(encrypted, "TestField");

        Assert.That(decrypted, Is.EqualTo(original));
    }

    [Test]
    public async Task EncryptFieldAsync_SameInputTwice_ProducesDifferentCiphertext()
    {
        var input = "determinism check";

        var encrypted1 = await _service.EncryptFieldAsync(input, "TestField");
        var encrypted2 = await _service.EncryptFieldAsync(input, "TestField");

        // Due to random nonce, same input should produce different ciphertext
        Assert.That(encrypted1, Is.Not.EqualTo(encrypted2));
    }

    [Test]
    public async Task EncryptFieldAsync_KeyProviderThrows_WrapsInFieldEncryptionException()
    {
        _mockKeyProvider.Setup(static kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Key vault unreachable"));

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.EncryptFieldAsync("test", "TestField"));
    }
}
