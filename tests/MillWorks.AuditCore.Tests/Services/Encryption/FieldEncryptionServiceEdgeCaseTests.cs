using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Security-focused edge case tests for <see cref="FieldEncryptionService"/> over the shared
/// MillWorks.Cryptography AES-256-GCM cipher. Validates round-trip fidelity, the ENC2 storage
/// envelope / AEAD frame structure, tamper detection, and boundary conditions.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public class FieldEncryptionServiceEdgeCaseTests
{
    private const string Prefix = "ENC2:";

    // ENC2 envelope: [envVersion:1][keyVersionLen:2 BE][keyVersion][frame]
    // AEAD frame:    [frameVersion:1][nonce:12][tag:16][ciphertext]
    private const int FrameNonceSize = 12;
    private const int FrameTagSize = 16;
    private const int FrameHeaderSize = 1 + FrameNonceSize + FrameTagSize;

    private FakeEncryptionKeyProvider _keyProvider = null!;
    private IFieldEncryptionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _keyProvider = new FakeEncryptionKeyProvider { CurrentVersion = "v1" };
        _service = EncryptionTestHarness.CreateService(_keyProvider);
    }

    // ── Round-trip with special content ──

    [Test]
    public async Task EncryptDecrypt_UnicodeContent_PreservesExactValue()
    {
        var original = "Hello \U0001F600 éèê 你好 АБВ ☃❤✌";
        var encrypted = await _service.EncryptFieldAsync(original, "UnicodeField");
        var decrypted = await _service.DecryptFieldAsync(encrypted, "UnicodeField");

        decrypted.Should().Be(original);
    }

    [Test]
    public async Task EncryptDecrypt_EmojiSequences_PreservesExactValue()
    {
        var original = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466 \U0001F3F3️‍\U0001F308 \U0001F1FA\U0001F1F8";
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

        FrameNonce(enc1).Should().NotEqual(FrameNonce(enc2), "nonces must differ for IV uniqueness");
    }

    // ── Tamper detection ──

    [Test]
    public async Task DecryptFieldAsync_WrongKey_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Swap the v1 key out so decryption resolves a different key.
        var wrongKey = new byte[32];
        Array.Fill(wrongKey, (byte)0x5A);
        _keyProvider.SetKey("TestField", "v1", wrongKey);

        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*tampered*");
    }

    [Test]
    public async Task DecryptFieldAsync_CorruptedCiphertext_FlippedBit_ThrowsAuthenticationFailure()
    {
        var encrypted = await _service.EncryptFieldAsync("integrity check", "TestField");

        var frame = Frame(encrypted);
        frame[FrameHeaderSize] ^= 0x01; // flip a ciphertext bit
        var tampered = ReplaceFrame(encrypted, frame);

        var act = () => _service.DecryptFieldAsync(tampered, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_TruncatedCiphertext_Throws()
    {
        var encrypted = await _service.EncryptFieldAsync("truncation test", "TestField");

        var frame = Frame(encrypted);
        var truncated = frame[..(FrameHeaderSize + ((frame.Length - FrameHeaderSize) / 2))];
        var tampered = ReplaceFrame(encrypted, truncated);

        var act = () => _service.DecryptFieldAsync(tampered, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_CorruptedTag_Throws()
    {
        var encrypted = await _service.EncryptFieldAsync("tag check", "TestField");

        var frame = Frame(encrypted);
        frame[1 + FrameNonceSize] ^= 0xFF; // flip a tag byte
        var tampered = ReplaceFrame(encrypted, frame);

        var act = () => _service.DecryptFieldAsync(tampered, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    // ── Envelope / frame structure validation ──

    [Test]
    public async Task EncryptedEnvelope_HasExpectedStructure()
    {
        var encrypted = await _service.EncryptFieldAsync("payload check", "TestField");

        encrypted.Should().StartWith(Prefix);

        var envelope = Envelope(encrypted);
        envelope[0].Should().Be(1, "the storage-envelope version byte is 1");
        EnvelopeKeyVersion(encrypted).Should().Be("v1");

        var frame = Frame(encrypted);
        frame[0].Should().Be(1, "the AEAD frame version byte is 1");
        frame.Length.Should().BeGreaterThan(FrameHeaderSize, "a non-empty plaintext yields ciphertext bytes");
    }

    [Test]
    public async Task EncryptedFrame_NonceSizeIs12Bytes()
    {
        var encrypted = await _service.EncryptFieldAsync("nonce size", "TestField");

        FrameNonce(encrypted).Length.Should().Be(12, "AES-GCM requires a 96-bit (12-byte) nonce");
    }

    [Test]
    public async Task EncryptedFrame_TagSizeIs16Bytes()
    {
        var encrypted = await _service.EncryptFieldAsync("tag size", "TestField");

        var frame = Frame(encrypted);
        frame[1..(1 + FrameNonceSize)].Length.Should().Be(FrameNonceSize);
        frame[(1 + FrameNonceSize)..(FrameHeaderSize)].Length.Should().Be(16, "AES-GCM uses a 128-bit (16-byte) auth tag");
    }

    // ── Key derivation / field binding ──

    [Test]
    public async Task DifferentFieldNames_ProduceDifferentCiphertext_AndAreNotInterchangeable()
    {
        var value = "same data";
        var encA = await _service.EncryptFieldAsync(value, "FieldA");
        var encB = await _service.EncryptFieldAsync(value, "FieldB");

        encA.Should().NotBe(encB, "different fields derive different keys and bind different AAD");

        // A value encrypted for FieldA cannot be decrypted as FieldB (different key + AAD).
        var act = () => _service.DecryptFieldAsync(encA, "FieldB");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_FieldNameMismatch_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("test", "CorrectField");

        var act = () => _service.DecryptFieldAsync(encrypted, "WrongField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    // ── No plaintext leakage in exceptions ──

    [Test]
    public async Task EncryptionException_DoesNotLeakPlaintext()
    {
        _keyProvider.ThrowOnGetVersion = new InvalidOperationException("Key vault down");

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
        var original = "ÀÁÂÃ \U0001F4A9";
        var encrypted = _service.EncryptField(original, "SyncUnicode");
        var decrypted = _service.DecryptField(encrypted, "SyncUnicode");

        decrypted.Should().Be(original);
    }

    [Test]
    public void DecryptField_Sync_CorruptedPayload_Throws()
    {
        var corrupted = Prefix + Convert.ToBase64String("not-a-valid-envelope"u8.ToArray());

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
        var original = "re-encrypt me \U0001F512";
        var encV1 = await _service.EncryptFieldAsync(original, "TestField");
        var encV2 = await _service.ReEncryptFieldAsync(encV1, "TestField", "v2");

        EnvelopeKeyVersion(encV2).Should().Be("v2");
        var decrypted = await _service.DecryptFieldAsync(encV2, "TestField");
        decrypted.Should().Be(original);
    }

    // ── Helpers: ENC2 envelope / AEAD frame parsing ──

    private static byte[] Envelope(string encrypted) =>
        Convert.FromBase64String(encrypted[Prefix.Length..]);

    private static int KeyVersionLength(byte[] envelope) => (envelope[1] << 8) | envelope[2];

    private static string EnvelopeKeyVersion(string encrypted)
    {
        var envelope = Envelope(encrypted);
        return System.Text.Encoding.UTF8.GetString(envelope, 3, KeyVersionLength(envelope));
    }

    private static byte[] Frame(string encrypted)
    {
        var envelope = Envelope(encrypted);
        return envelope[(3 + KeyVersionLength(envelope))..];
    }

    private static byte[] FrameNonce(string encrypted) => Frame(encrypted)[1..(1 + FrameNonceSize)];

    private static string ReplaceFrame(string encrypted, byte[] newFrame)
    {
        var envelope = Envelope(encrypted);
        var head = envelope[..(3 + KeyVersionLength(envelope))];
        var combined = new byte[head.Length + newFrame.Length];
        head.CopyTo(combined, 0);
        newFrame.CopyTo(combined, head.Length);
        return Prefix + Convert.ToBase64String(combined);
    }
}
