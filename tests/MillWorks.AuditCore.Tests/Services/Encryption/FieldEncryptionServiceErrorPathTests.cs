using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Tests for FieldEncryptionService error paths: ReEncryptFieldAsync failures,
/// key provider errors during decrypt, invalid Base64 payloads, sync CryptographicException,
/// and field name edge cases — all previously at low or zero coverage.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FieldEncryptionServiceErrorPathTests
{
    private Mock<IEncryptionKeyProvider> _mockKeyProvider;
    private Mock<ILogger<FieldEncryptionService>> _mockLogger;
    private FieldEncryptionService _service;

    private static readonly byte[] TestKey =
    {
        0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08,
        0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10,
        0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18,
        0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20
    };

    private static readonly byte[] AlternateKey =
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

        _mockKeyProvider
            .Setup(static kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("v1");
        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);
        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlternateKey);

        _mockKeyProvider.Setup(static kp => kp.GetCurrentKeyVersion()).Returns("v1");
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKey(It.IsAny<string>(), "v1")).Returns(TestKey);
        _mockKeyProvider.Setup(static kp => kp.GetEncryptionKey(It.IsAny<string>(), "v2")).Returns(AlternateKey);

        _service = new FieldEncryptionService(_mockKeyProvider.Object, _mockLogger.Object);
    }

    #region ReEncryptFieldAsync — Error Paths

    [Test]
    public async Task ReEncryptFieldAsync_WhenDecryptFails_ThrowsFieldEncryptionException()
    {
        // Arrange — encrypt with v1, then make v1 key unavailable for decrypt
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Key v1 has been retired"));

        // Act & Assert
        var act = () => _service.ReEncryptFieldAsync(encrypted, "TestField", "v2");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*re-encrypt*TestField*v2*");
    }

    [Test]
    public async Task ReEncryptFieldAsync_WhenEncryptWithNewVersionFails_ThrowsFieldEncryptionException()
    {
        // Arrange — encrypt with v1, decrypt will work, but v3 key doesn't exist
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v3", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Key version v3 not found"));

        // Act & Assert
        var act = () => _service.ReEncryptFieldAsync(encrypted, "TestField", "v3");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*re-encrypt*TestField*v3*");
    }

    [Test]
    public async Task ReEncryptFieldAsync_WithTamperedData_ThrowsFieldEncryptionException()
    {
        // Arrange — encrypt then corrupt the ciphertext so decrypt fails with CryptographicException
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Corrupt a byte in the payload
        var payloadBase64 = encrypted["ENC_V1:".Length..];
        var payloadBytes = Convert.FromBase64String(payloadBase64);
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!;

        // Corrupt the ciphertext
        var cipherBase64 = payload["Ciphertext"].ToString()!;
        var cipherBytes = Convert.FromBase64String(cipherBase64);
        cipherBytes[0] ^= 0xFF;
        payload["Ciphertext"] = Convert.ToBase64String(cipherBytes);

        var corruptedJson = JsonSerializer.Serialize(payload);
        var corruptedEncrypted = "ENC_V1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(corruptedJson));

        // Act & Assert
        var act = () => _service.ReEncryptFieldAsync(corruptedEncrypted, "TestField", "v2");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    #endregion

    #region DecryptFieldAsync — Key Provider Errors

    [Test]
    public async Task DecryptFieldAsync_WhenKeyProviderThrows_ThrowsFieldEncryptionException()
    {
        // Arrange
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Make key provider fail for the version used in the payload
        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Key storage unavailable"));

        // Act & Assert
        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*decrypt*TestField*");
    }

    [Test]
    public async Task DecryptField_Sync_WhenKeyProviderThrows_ThrowsFieldEncryptionException()
    {
        // Arrange
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Make sync key provider fail
        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKey(It.IsAny<string>(), "v1"))
            .Throws(new InvalidOperationException("Key storage unavailable"));

        // Act & Assert
        var act = () => _service.DecryptField(encrypted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*decrypt*TestField*");
    }

    #endregion

    #region DecryptFieldAsync — Invalid Base64 Payloads

    [Test]
    public void DecryptFieldAsync_WithInvalidBase64Payload_ThrowsFieldEncryptionException()
    {
        // Arrange — prefix followed by invalid Base64
        var badValue = "ENC_V1:not-valid-base64!!!@@@";

        // Act & Assert
        var act = () => _service.DecryptFieldAsync(badValue, "TestField");
        act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_WithMalformedJsonPayload_ThrowsFieldEncryptionException()
    {
        // Arrange — valid Base64 but not valid JSON inside
        var badJson = "this is not JSON";
        var encoded = "ENC_V1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(badJson));

        // Act & Assert
        var act = () => _service.DecryptFieldAsync(encoded, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_WithInvalidBase64InNonce_ThrowsFieldEncryptionException()
    {
        // Arrange — valid outer payload but nonce has bad Base64
        var payload = new
        {
            Version = 1,
            KeyVersion = "v1",
            Nonce = "not-valid-base64!!!",
            Ciphertext = Convert.ToBase64String(new byte[16]),
            Tag = Convert.ToBase64String(new byte[16]),
            FieldName = "TestField",
            EncryptedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(payload);
        var encoded = "ENC_V1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

        // Act & Assert
        var act = () => _service.DecryptFieldAsync(encoded, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    #endregion

    #region Sync CryptographicException Path

    [Test]
    public async Task DecryptField_Sync_WithWrongKey_ThrowsTamperMessage()
    {
        // Arrange — encrypt with TestKey, then swap to WrongKey for sync decrypt
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKey(It.IsAny<string>(), "v1"))
            .Returns(AlternateKey); // Wrong key

        // Act & Assert — hits the CryptographicException catch block in DecryptField
        var act = () => _service.DecryptField(encrypted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*tampered*");
    }

    [Test]
    public async Task DecryptField_Sync_WithCorruptedTag_ThrowsTamperMessage()
    {
        // Arrange
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var payloadBase64 = encrypted["ENC_V1:".Length..];
        var payloadBytes = Convert.FromBase64String(payloadBase64);
        var payloadJson = Encoding.UTF8.GetString(payloadBytes);
        var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(payloadJson)!;

        // Corrupt the authentication tag
        var tagBytes = Convert.FromBase64String(dict["Tag"].ToString()!);
        tagBytes[0] ^= 0xFF;
        dict["Tag"] = Convert.ToBase64String(tagBytes);

        var corruptedJson = JsonSerializer.Serialize(dict);
        var corruptedEncrypted = "ENC_V1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(corruptedJson));

        // Act & Assert — sync path CryptographicException
        var act = () => _service.DecryptField(corruptedEncrypted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*tampered*");
    }

    #endregion

    #region Field Name Edge Cases

    [Test]
    public async Task DecryptFieldAsync_WithSpecialCharactersInFieldName_RoundTrips()
    {
        // Arrange — field names with dots, brackets, etc.
        const string fieldName = "user.addresses[0].street";

        _mockKeyProvider
            .Setup(kp => kp.GetEncryptionKeyAsync(fieldName, "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);

        // Act
        var encrypted = await _service.EncryptFieldWithVersionAsync("123 Main St", fieldName, "v1");
        var decrypted = await _service.DecryptFieldAsync(encrypted, fieldName);

        // Assert
        decrypted.Should().Be("123 Main St");
    }

    [Test]
    public async Task DecryptFieldAsync_WithWhitespaceFieldNameMismatch_Throws()
    {
        // Arrange — encrypt with "TestField", try to decrypt with " TestField "
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Act & Assert — Ordinal comparison means whitespace causes mismatch.
        // FieldEncryptionException is thrown directly (not wrapped).
        var act = () => _service.DecryptFieldAsync(encrypted, " TestField ");
        var ex = await act.Should().ThrowAsync<FieldEncryptionException>();
        ex.WithMessage("*mismatch*");
    }

    [Test]
    public async Task DecryptField_Sync_FieldNameMismatch_ThrowsSameAsAsync()
    {
        // Arrange
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Act & Assert — FieldEncryptionException is thrown directly (not wrapped).
        var act = () => _service.DecryptField(encrypted, "WrongField");
        var ex = act.Should().Throw<FieldEncryptionException>();
        ex.WithMessage("*mismatch*");
    }

    #endregion

    #region EncryptFieldAsync — Key Provider GetCurrentKeyVersion Error

    [Test]
    public void EncryptFieldAsync_WhenGetCurrentVersionThrows_ThrowsFieldEncryptionException()
    {
        // Arrange
        _mockKeyProvider
            .Setup(static kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("No key versions available"));

        // Act & Assert
        var act = () => _service.EncryptFieldAsync("secret", "TestField");
        act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*encrypt*TestField*");
    }

    [Test]
    public void EncryptField_Sync_WhenGetCurrentVersionThrows_ThrowsFieldEncryptionException()
    {
        // Arrange
        _mockKeyProvider
            .Setup(static kp => kp.GetCurrentKeyVersion())
            .Throws(new InvalidOperationException("No key versions available"));

        // Act & Assert
        var act = () => _service.EncryptField("secret", "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*encrypt*TestField*");
    }

    #endregion

    #region Async CryptographicException — Tamper Message Validation

    [Test]
    public async Task DecryptFieldAsync_WithWrongKey_ThrowsWithTamperMessage()
    {
        // Arrange
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _mockKeyProvider
            .Setup(static kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(AlternateKey);

        // Act & Assert — validates the CryptographicException path produces the "tampered" message
        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        var ex = await act.Should().ThrowAsync<FieldEncryptionException>();
        ex.WithMessage("*tampered*");
        ex.WithInnerException<System.Security.Cryptography.CryptographicException>();
    }

    #endregion

    #region IsEncrypted — Boundary Cases

    [Test]
    public void IsEncrypted_WithPrefixOnly_ReturnsTrue()
    {
        _service.IsEncrypted("ENC_V1:").Should().BeTrue();
    }

    [Test]
    public void IsEncrypted_WithSimilarButWrongPrefix_ReturnsFalse()
    {
        _service.IsEncrypted("ENC_V2:something").Should().BeFalse();
        _service.IsEncrypted("enc_v1:lowercase").Should().BeFalse();
        _service.IsEncrypted("ENC_V1").Should().BeFalse(); // Missing colon
    }

    #endregion
}
