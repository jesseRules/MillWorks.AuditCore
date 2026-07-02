using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Unit tests for <see cref="FieldEncryptionService"/> over the shared MillWorks.Cryptography AEAD
/// primitive. A <see cref="FakeEncryptionKeyProvider"/> stands in for key storage while the real
/// AES-256-GCM cipher and the ENC2 storage envelope are exercised end-to-end.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FieldEncryptionServiceTests
{
    private const string Prefix = "ENC2:";

    private FakeEncryptionKeyProvider _keyProvider = null!;
    private IFieldEncryptionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _keyProvider = new FakeEncryptionKeyProvider { CurrentVersion = "v1" };
        _service = EncryptionTestHarness.CreateService(_keyProvider);
    }

    [Test]
    public async Task EncryptFieldAsync_PlainText_ReturnsEncryptedPayloadWithPrefix()
    {
        var encrypted = await _service.EncryptFieldAsync("Hello, World!", "TestField");

        Assert.That(encrypted, Does.StartWith(Prefix));
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

        Assert.That(encrypted, Does.StartWith(Prefix));
        Assert.That(ReadEnvelopeKeyVersion(encrypted), Is.EqualTo("v1"),
            "the key version must be carried in the storage envelope so decryption can resolve it");

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

        // The ciphertext should be different, and the envelope should now carry v2.
        Assert.That(encryptedV2, Is.Not.EqualTo(encryptedV1));
        Assert.That(ReadEnvelopeKeyVersion(encryptedV2), Is.EqualTo("v2"));

        // Decrypting (which resolves v2 from the envelope) returns the original.
        var decrypted = await _service.DecryptFieldAsync(encryptedV2, "TestField");
        Assert.That(decrypted, Is.EqualTo(original));
    }

    [Test]
    public async Task DecryptFieldAsync_WrongKey_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Rotate the v1 key out from under the ciphertext: decryption now resolves a different key.
        var wrongKey = new byte[32];
        Array.Fill(wrongKey, (byte)0xAB);
        _keyProvider.SetKey("TestField", "v1", wrongKey);

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(encrypted, "TestField"));
    }

    [Test]
    public void DecryptFieldAsync_CorruptedPayload_ThrowsFieldEncryptionException()
    {
        var corruptedPayload = Prefix + Convert.ToBase64String("not-a-valid-envelope"u8.ToArray());

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
        // Encrypted for "TestField"; decrypting as "OtherField" resolves a different field key and a
        // different AAD binding, so AEAD authentication fails — a cryptographically enforced check.
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

        // Due to the random per-call nonce, the same input yields different frames.
        Assert.That(encrypted1, Is.Not.EqualTo(encrypted2));
    }

    [Test]
    public void EncryptFieldAsync_KeyProviderThrows_WrapsInFieldEncryptionException()
    {
        _keyProvider.ThrowOnGetVersion = new InvalidOperationException("Key vault unreachable");

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.EncryptFieldAsync("test", "TestField"));
    }

    #region AAD / envelope tamper tests

    [Test]
    public async Task DecryptFieldAsync_WithTamperedKeyVersion_ThrowsFieldEncryptionException()
    {
        // Encrypt with v1, then flip the key-version byte carried in the envelope to "v9". Decryption
        // resolves the v9 key and binds v9 into the AAD, so GCM authentication fails.
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var envelope = DecodeEnvelope(encrypted);
        // Envelope: [envVer:1][kvLen:2 BE]["v1"][frame]; the second key-version char sits at index 4.
        envelope[4] = (byte)'9';
        var tampered = EncodeEnvelope(envelope);

        Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(tampered, "TestField"));
    }

    [Test]
    public async Task DecryptFieldAsync_WithTamperedEnvelopeVersion_ThrowsVersionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var envelope = DecodeEnvelope(encrypted);
        envelope[0] = 99; // unsupported storage-envelope version
        var tampered = EncodeEnvelope(envelope);

        var ex = Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(tampered, "TestField"));
        Assert.That(ex!.Message, Does.Contain("Unsupported encryption envelope version"));
    }

    [Test]
    public void DecryptField_Sync_WithTamperedEnvelopeVersion_ThrowsVersionException()
    {
        var encrypted = _service.EncryptField("secret", "TestField");

        var envelope = DecodeEnvelope(encrypted);
        envelope[0] = 99;
        var tampered = EncodeEnvelope(envelope);

        var ex = Assert.Throws<FieldEncryptionException>(() =>
            _service.DecryptField(tampered, "TestField"));
        Assert.That(ex!.Message, Does.Contain("Unsupported encryption envelope version"));
    }

    [Test]
    public async Task DecryptFieldAsync_WithFlippedCiphertextBit_ThrowsTamperException()
    {
        var encrypted = await _service.EncryptFieldAsync("integrity", "TestField");

        var envelope = DecodeEnvelope(encrypted);
        // Frame starts after [envVer:1][kvLen:2]["v1"] = index 5; ciphertext begins after the frame's
        // [version:1][nonce:12][tag:16] header = +29. Flip a ciphertext bit.
        envelope[5 + 29] ^= 0x01;
        var tampered = EncodeEnvelope(envelope);

        var ex = Assert.ThrowsAsync<FieldEncryptionException>(async () =>
            await _service.DecryptFieldAsync(tampered, "TestField"));
        Assert.That(ex!.Message, Does.Contain("tampered"));
    }

    [Test]
    public void IsEncrypted_UsesOrdinalComparison()
    {
        // Case-sensitive (ordinal): only the exact "ENC2:" sentinel counts as encrypted.
        Assert.That(_service.IsEncrypted("enc2:lowercase"), Is.False);
        Assert.That(_service.IsEncrypted("Enc2:mixedcase"), Is.False);
        Assert.That(_service.IsEncrypted("ENC2:valid"), Is.True);
    }

    #endregion

    private static byte[] DecodeEnvelope(string encrypted) =>
        Convert.FromBase64String(encrypted[Prefix.Length..]);

    private static string EncodeEnvelope(byte[] envelope) =>
        Prefix + Convert.ToBase64String(envelope);

    private static string ReadEnvelopeKeyVersion(string encrypted)
    {
        var envelope = DecodeEnvelope(encrypted);
        var kvLen = (envelope[1] << 8) | envelope[2];
        return System.Text.Encoding.UTF8.GetString(envelope, 3, kvLen);
    }
}
