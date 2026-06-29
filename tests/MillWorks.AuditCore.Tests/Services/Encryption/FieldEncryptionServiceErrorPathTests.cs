using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Services.Encryption;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.Cryptography;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

/// <summary>
/// Error-path tests for <see cref="FieldEncryptionService"/>: re-encryption failures, key-provider
/// errors during decrypt, malformed envelopes, the synchronous tamper path, and field-name binding —
/// over the shared MillWorks.Cryptography AEAD cipher with a faked key store.
/// </summary>
[TestFixture]
[Category("Unit")]
public class FieldEncryptionServiceErrorPathTests
{
    private const string Prefix = "ENC2:";
    private const int FrameHeaderSize = 1 + 12 + 16; // [version][nonce:12][tag:16]

    private FakeEncryptionKeyProvider _keyProvider = null!;
    private IFieldEncryptionService _service = null!;

    [SetUp]
    public void Setup()
    {
        _keyProvider = new FakeEncryptionKeyProvider { CurrentVersion = "v1" };
        _service = EncryptionTestHarness.CreateService(_keyProvider);
    }

    #region ReEncryptFieldAsync — Error Paths

    [Test]
    public async Task ReEncryptFieldAsync_WhenDecryptFails_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        // Make the key store unreachable so the inner decrypt fails.
        _keyProvider.ThrowOnGetKey = new InvalidOperationException("Key store unreachable");

        var act = () => _service.ReEncryptFieldAsync(encrypted, "TestField", "v2");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*re-encrypt*TestField*v2*");
    }

    [Test]
    public async Task ReEncryptFieldAsync_WhenEncryptWithNewVersionFails_ThrowsFieldEncryptionException()
    {
        // Decrypt with v1 works, but the target version v3 is unavailable.
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");
        _keyProvider.FailingVersions.Add("v3");

        var act = () => _service.ReEncryptFieldAsync(encrypted, "TestField", "v3");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*re-encrypt*TestField*v3*");
    }

    [Test]
    public async Task ReEncryptFieldAsync_WithTamperedData_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var frame = Frame(encrypted);
        frame[FrameHeaderSize] ^= 0xFF; // corrupt a ciphertext byte
        var corrupted = ReplaceFrame(encrypted, frame);

        var act = () => _service.ReEncryptFieldAsync(corrupted, "TestField", "v2");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    #endregion

    #region DecryptFieldAsync — Key Provider Errors

    [Test]
    public async Task DecryptFieldAsync_WhenKeyProviderThrows_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _keyProvider.ThrowOnGetKey = new InvalidOperationException("Key storage unavailable");

        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*decrypt*TestField*");
    }

    [Test]
    public async Task DecryptField_Sync_WhenKeyProviderThrows_ThrowsFieldEncryptionException()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        _keyProvider.ThrowOnGetKey = new InvalidOperationException("Key storage unavailable");

        var act = () => _service.DecryptField(encrypted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*decrypt*TestField*");
    }

    #endregion

    #region DecryptFieldAsync — Malformed Envelopes

    [Test]
    public async Task DecryptFieldAsync_WithInvalidBase64Payload_ThrowsFieldEncryptionException()
    {
        var badValue = Prefix + "not-valid-base64!!!@@@";

        var act = () => _service.DecryptFieldAsync(badValue, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    [Test]
    public async Task DecryptFieldAsync_WithTruncatedEnvelope_ThrowsFieldEncryptionException()
    {
        // Valid Base64, but too short to hold the [version][keyVersionLen] header.
        var encoded = Prefix + Convert.ToBase64String(new byte[] { 1 });

        var act = () => _service.DecryptFieldAsync(encoded, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*truncated*");
    }

    [Test]
    public async Task DecryptFieldAsync_WithTruncatedFrame_ThrowsFieldEncryptionException()
    {
        // Well-formed envelope wrapping a frame that is too short to be a valid AEAD frame.
        var envelope = new byte[] { 1, 0, 2, (byte)'v', (byte)'1', 1, 2, 3, 4, 5 };
        var encoded = Prefix + Convert.ToBase64String(envelope);

        var act = () => _service.DecryptFieldAsync(encoded, "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>();
    }

    #endregion

    #region Synchronous tamper path

    [Test]
    public async Task DecryptField_Sync_WithWrongKey_ThrowsTamperMessage()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var wrongKey = new byte[32];
        Array.Fill(wrongKey, (byte)0x7E);
        _keyProvider.SetKey("TestField", "v1", wrongKey);

        var act = () => _service.DecryptField(encrypted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*tampered*")
            .WithInnerException<CryptographyException>();
    }

    [Test]
    public async Task DecryptField_Sync_WithCorruptedTag_ThrowsTamperMessage()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var frame = Frame(encrypted);
        frame[1 + 12] ^= 0xFF; // flip a byte in the 16-byte auth tag
        var corrupted = ReplaceFrame(encrypted, frame);

        var act = () => _service.DecryptField(corrupted, "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*tampered*");
    }

    #endregion

    #region Field Name Binding

    [Test]
    public async Task DecryptFieldAsync_WithSpecialCharactersInFieldName_RoundTrips()
    {
        const string fieldName = "user.addresses[0].street";

        var encrypted = await _service.EncryptFieldWithVersionAsync("123 Main St", fieldName, "v1");
        var decrypted = await _service.DecryptFieldAsync(encrypted, fieldName);

        decrypted.Should().Be("123 Main St");
    }

    [Test]
    public async Task DecryptFieldAsync_WithWhitespaceFieldNameMismatch_Throws()
    {
        // " TestField " binds a different AAD (and resolves a different field key) than "TestField",
        // so AEAD authentication fails — surfaced as the tamper message.
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var act = () => _service.DecryptFieldAsync(encrypted, " TestField ");
        await act.Should().ThrowAsync<FieldEncryptionException>().WithMessage("*tampered*");
    }

    [Test]
    public async Task DecryptField_Sync_FieldNameMismatch_Throws()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var act = () => _service.DecryptField(encrypted, "WrongField");
        act.Should().Throw<FieldEncryptionException>().WithMessage("*tampered*");
    }

    #endregion

    #region EncryptFieldAsync — GetCurrentVersion Error

    [Test]
    public async Task EncryptFieldAsync_WhenGetCurrentVersionThrows_ThrowsFieldEncryptionException()
    {
        _keyProvider.ThrowOnGetVersion = new InvalidOperationException("No key versions available");

        var act = () => _service.EncryptFieldAsync("secret", "TestField");
        await act.Should().ThrowAsync<FieldEncryptionException>()
            .WithMessage("*encrypt*TestField*");
    }

    [Test]
    public void EncryptField_Sync_WhenGetCurrentVersionThrows_ThrowsFieldEncryptionException()
    {
        _keyProvider.ThrowOnGetVersion = new InvalidOperationException("No key versions available");

        var act = () => _service.EncryptField("secret", "TestField");
        act.Should().Throw<FieldEncryptionException>()
            .WithMessage("*encrypt*TestField*");
    }

    #endregion

    #region Async tamper message validation

    [Test]
    public async Task DecryptFieldAsync_WithWrongKey_ThrowsWithTamperMessage()
    {
        var encrypted = await _service.EncryptFieldAsync("secret", "TestField");

        var wrongKey = new byte[32];
        Array.Fill(wrongKey, (byte)0x33);
        _keyProvider.SetKey("TestField", "v1", wrongKey);

        var act = () => _service.DecryptFieldAsync(encrypted, "TestField");
        var assertion = await act.Should().ThrowAsync<FieldEncryptionException>();
        assertion.WithMessage("*tampered*");
        assertion.WithInnerException<CryptographyException>();
    }

    #endregion

    #region IsEncrypted — Boundary Cases

    [Test]
    public void IsEncrypted_WithPrefixOnly_ReturnsTrue()
    {
        _service.IsEncrypted(Prefix).Should().BeTrue();
    }

    [Test]
    public void IsEncrypted_WithSimilarButWrongPrefix_ReturnsFalse()
    {
        _service.IsEncrypted("ENC3:something").Should().BeFalse();
        _service.IsEncrypted("enc2:lowercase").Should().BeFalse();
        _service.IsEncrypted("ENC2").Should().BeFalse(); // missing colon
    }

    #endregion

    // ── Helpers: ENC2 envelope / AEAD frame parsing ──

    private static byte[] Envelope(string encrypted) =>
        Convert.FromBase64String(encrypted[Prefix.Length..]);

    private static int KeyVersionLength(byte[] envelope) => (envelope[1] << 8) | envelope[2];

    private static byte[] Frame(string encrypted)
    {
        var envelope = Envelope(encrypted);
        return envelope[(3 + KeyVersionLength(envelope))..];
    }

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
