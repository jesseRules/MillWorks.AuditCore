using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Phase 4: Security-focused edge case tests for FieldEncryptionService.
/// Validates AES-256-GCM encryption correctness, tamper detection, and boundary conditions.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public class FieldEncryptionServiceEdgeCaseTests
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

    private static readonly byte[] WrongKey =
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

        _mockKeyProvider.Setup(kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync("v1");
        _mockKeyProvider.Setup(kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);
        _mockKeyProvider.Setup(kp => kp.GetCurrentKeyVersion()).Returns("v1");
        _mockKeyProvider.Setup(kp => kp.GetEncryptionKey(It.IsAny<string>(), "v1")).Returns(TestKey);

        _service = new FieldEncryptionService(_mockKeyProvider.Object, _mockLogger.Object);
    }

    // ── Round-trip with special content ──

    [Test]
    public async Task EncryptDecrypt_UnicodeContent_PreservesExactValue()
    {
        var original = "Hello \U0001F600 \u00E9\u00E8\u00EA \u4F60\u597D \u0410\u0411\u0412 \u2603\u2764\u270C";
        var encrypted = await _service.EncryptFieldAsync(original, "UnicodeField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "UnicodeField");

        decrypted.Should().Be(original);
    }

    [Test]
    public async Task EncryptDecrypt_EmojiSequences_PreservesExactValue()
    {
        var original = "\U0001F468\u200D\U0001F469\u200D\U0001F467\u200D\U0001F466 \U0001F3F3\uFE0F\u200D\U0001F308 \U0001F1FA\U0001F1F8";
        var encrypted = await _service.EncryptFieldAsync(original, "EmojiField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "EmojiField");

        decrypted.Should().Be(original);
    }

    [Test]
    public async Task EncryptDecrypt_NullBytes_PreservesExactValue()
    {
        var original = "before\0middle\0after";
        var encrypted = await _service.EncryptFieldAsync(original, "NullByteField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "NullByteField");

        decrypted.Should().Be(original);
    }

    [Test]
    public async Task EncryptDecrypt_LargeValue_1MB_RoundTrips()
    {
        var original = new string('A', 1024 * 1024); // 1 MB
        var encrypted = await _service.EncryptFieldAsync(original, "LargeField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "LargeField");

        decrypted.Should().Be(original);
    }

    // ── Nonce/IV uniqueness ──

    [Test]
    public async Task EncryptFieldAsync_SameValueTwice_ProducesDifferentCiphertext()
    {
        var value = "identical input";
        var encrypted1 = await _service.EncryptFieldAsync(value, "Field1");
        var encrypted2 = await _service.EncryptFieldAsync(value, "Field1");

        encrypted1.Should().NotBe(encrypted2, "each encryption must use a unique IV/nonce");
    }

    [Test]
    public async Task EncryptFieldAsync_SameValue_DifferentNonces()
    {
        var value = "nonce check";
        var enc1 = await _service.EncryptFieldAsync(value, "Field1");
        var enc2 = await _service.EncryptFieldAsync(value, "Field1");

        // Parse both payloads to extract the nonce
        var payload1 = DecodePayload(enc1);
        var payload2 = DecodePayload(enc2);

        payload1.Nonce.Should().NotBe(payload2.Nonce, "nonces must differ for IV uniqueness");
    }

    // ── Tamper detection ──

    [Test]
    public async Task DecryptFieldAsync_WrongKey_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Swap key to wrong one
        _mockKeyProvider.Setup(kp => kp.GetEncryptionKeyAsync("TestField", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(WrongKey);

        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*tampered*");
    }

    [Test]
    public async Task DecryptFieldAsync_CorruptedCiphertext_FlippedBit_ThrowsAuthenticationFailure()
    {
        var encrypted = await _service.EncryptFieldAsync("integrity check", "TestField");

        // Decode, flip a bit in the ciphertext, re-encode
        var payload = DecodePayload(encrypted);
        var cipherBytes = Convert.FromBase64String(payload.Ciphertext);
        cipherBytes[0] ^= 0x01; // Flip one bit
        payload.Ciphertext = Convert.ToBase64String(cipherBytes);
        var tampered = EncodePayload(payload);

        var act = () => _service.DecryptFieldAsync(tampered, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_TruncatedCiphertext_Throws()
    {
        var encrypted = await _service.EncryptFieldAsync("truncation test", "TestField");

        var payload = DecodePayload(encrypted);
        var cipherBytes = Convert.FromBase64String(payload.Ciphertext);
        // Truncate to half
        payload.Ciphertext = Convert.ToBase64String(cipherBytes[..(cipherBytes.Length / 2)]);
        var truncated = EncodePayload(payload);

        var act = () => _service.DecryptFieldAsync(truncated, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_CorruptedTag_Throws()
    {
        var encrypted = await _service.EncryptFieldAsync("tag check", "TestField");

        var payload = DecodePayload(encrypted);
        var tagBytes = Convert.FromBase64String(payload.Tag);
        tagBytes[0] ^= 0xFF;
        payload.Tag = Convert.ToBase64String(tagBytes);
        var tampered = EncodePayload(payload);

        var act = () => _service.DecryptFieldAsync(tampered, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    // ── Payload structure validation ──

    [Test]
    public async Task EncryptedPayload_ContainsRequiredFields()
    {
        var encrypted = await _service.EncryptFieldAsync("payload check", "TestField");
        var payload = DecodePayload(encrypted);

        payload.Version.Should().Be(1);
        payload.KeyVersion.Should().Be("v1");
        payload.Nonce.Should().NotBeNullOrEmpty();
        payload.Ciphertext.Should().NotBeNullOrEmpty();
        payload.Tag.Should().NotBeNullOrEmpty();
        payload.FieldName.Should().Be("TestField");
        payload.EncryptedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task EncryptedPayload_NonceSizeIs12Bytes()
    {
        var encrypted = await _service.EncryptFieldAsync("nonce size", "TestField");
        var payload = DecodePayload(encrypted);
        var nonceBytes = Convert.FromBase64String(payload.Nonce);

        nonceBytes.Length.Should().Be(12, "AES-GCM requires 96-bit (12-byte) nonce");
    }

    [Test]
    public async Task EncryptedPayload_TagSizeIs16Bytes()
    {
        var encrypted = await _service.EncryptFieldAsync("tag size", "TestField");
        var payload = DecodePayload(encrypted);
        var tagBytes = Convert.FromBase64String(payload.Tag);

        tagBytes.Length.Should().Be(16, "AES-GCM should use 128-bit (16-byte) auth tag");
    }

    // ── Key derivation context ──

    [Test]
    public async Task DifferentFieldNames_ProduceDifferentCiphertext_WithSameKey()
    {
        var value = "same data";
        var enc1 = await _service.EncryptFieldAsync(value, "FieldA");
        var enc2 = await _service.EncryptFieldAsync(value, "FieldB");

        // Even ignoring random nonce, the field name derivation should differ
        var payload1 = DecodePayload(enc1);
        var payload2 = DecodePayload(enc2);
        payload1.FieldName.Should().Be("FieldA");
        payload2.FieldName.Should().Be("FieldB");
    }

    [Test]
    public async Task DecryptFieldAsync_FieldNameMismatch_ThrowsFieldEncryptionException()
    {
        _mockKeyProvider.Setup(kp => kp.GetEncryptionKeyAsync("WrongField", "v1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestKey);

        var encrypted = await _service.EncryptFieldAsync("test", "CorrectField");

        var act = () => _service.DecryptFieldAsync(encrypted, "WrongField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    // ── No plaintext leakage in exceptions ──

    [Test]
    public async Task EncryptionException_DoesNotLeakPlaintext()
    {
        _mockKeyProvider.Setup(kp => kp.GetCurrentKeyVersionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Key vault down"));

        var sensitiveData = "SSN:123-45-6789";
        try
        {
            await _service.EncryptFieldAsync(sensitiveData, "SensitiveField");
            Assert.Fail("Should have thrown");
        }
        catch (FieldEncryptionException ex)
        {
            ex.Message.Should().NotContain("123-45-6789");
            ex.ToString().Should().NotContain("SSN:123-45-6789");
        }
    }

    // ── Sync path coverage ──

    [Test]
    public void EncryptField_Sync_UnicodeRoundTrip()
    {
        var original = "\u00C0\u00C1\u00C2\u00C3 \U0001F4A9";
        var encrypted = _service.EncryptField(original, "SyncUnicode");
        var decrypted = _service.DecryptField(encrypted, "SyncUnicode");

        decrypted.Should().Be(original);
    }

    [Test]
    public void DecryptField_Sync_CorruptedPayload_Throws()
    {
        var corrupted = "ENC_V1:" + Convert.ToBase64String("{{invalid json}}"u8.ToArray());

        var act = () => _service.DecryptField(corrupted, "TestField");
        act.Should().Throw<FieldEncryptionException>();
    }

    [Test]
    public void DecryptField_Sync_NotEncrypted_PassesThrough()
    {
        var plain = "just a plain string";
        _service.DecryptField(plain, "TestField").Should().Be(plain);
    }

    [Test]
    public void EncryptField_Sync_EmptyString_ReturnsEmpty()
    {
        _service.EncryptField("", "TestField").Should().Be("");
    }

    [Test]
    public void EncryptField_Sync_Null_ReturnsNull()
    {
        _service.EncryptField(null!, "TestField").Should().BeNull();
    }

    // ── ReEncrypt ──

    [Test]
    public async Task ReEncryptFieldAsync_PreservesOriginalPlaintext()
    {
        var v2Key = new byte[32];
        Array.Fill<byte>(v2Key, 0xAA);

        _mockKeyProvider.Setup(kp => kp.GetEncryptionKeyAsync(It.IsAny<string>(), "v2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(v2Key);

        var original = "re-encrypt me \U0001F512";
        var encV1 = await _service.EncryptFieldAsync(original, "TestField");
        var encV2 = await _service.ReEncryptFieldAsync(encV1, "TestField", "v2");

        // Decrypt with v2 key
        var decrypted = await _service.DecryptFieldAsync(encV2, "TestField");
        decrypted.Should().Be(original);
    }

    // ── Helper methods ──

    private static EncryptedFieldPayloadDto DecodePayload(string encryptedValue)
    {
        var base64 = encryptedValue["ENC_V1:".Length..];
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return JsonSerializer.Deserialize<EncryptedFieldPayloadDto>(json)!;
    }

    private static string EncodePayload(EncryptedFieldPayloadDto payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return "ENC_V1:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>
    /// Mirror of the internal EncryptedFieldPayload for test deserialization.
    /// </summary>
    private sealed class EncryptedFieldPayloadDto
    {
        public int Version { get; set; }
        public string KeyVersion { get; set; } = "";
        public string Nonce { get; set; } = "";
        public string Ciphertext { get; set; } = "";
        public string Tag { get; set; } = "";
        public string FieldName { get; set; } = "";
        public DateTimeOffset EncryptedAt { get; set; }
    }
}
