using System.Security.Cryptography;
using MillWorks.Cryptography.Hashing;
using MillWorks.Cryptography.KeyManagement;
using MillWorks.Cryptography.Signing;

namespace MillWorks.AuditCore.Tests.Helpers;

/// <summary>
/// Test doubles for the <c>MillWorks.Cryptography</c> signing surface that
/// <c>TamperDetectionService</c> consumes, so unit/integration tests can build the service without a
/// file-system key backend. Each signer is backed by an <see cref="InMemorySigningKeyProvider"/> that
/// holds a single fixed key under a stable id; an unknown id resolves to <c>null</c> so cross-key
/// verification correctly fails.
/// </summary>
internal static class IntegrityTestCrypto
{
    /// <summary>Shared stateless SHA-2 hasher (event hash + checksum).</summary>
    public static IHasher Hasher { get; } = new Sha2Hasher();

    /// <summary>
    /// Builds an HMAC-SHA-256 signer over a fixed key. Defaults to a fresh random 32-byte key; pass an
    /// explicit <paramref name="key"/> / <paramref name="keyId"/> to share or distinguish keys.
    /// </summary>
    public static HmacSha256Signer CreateHmacSigner(byte[]? key = null, string keyId = "test-hmac-key")
    {
        var material = key ?? RandomNumberGenerator.GetBytes(32);
        return new HmacSha256Signer(new InMemorySigningKeyProvider(keyId, material), Hasher);
    }

    /// <summary>
    /// Builds an RSA-PSS signer over a fixed RSA private key (PKCS#8). Defaults to a fresh RSA-2048 key;
    /// pass an explicit <paramref name="rsa"/> / <paramref name="keyId"/> to share or distinguish keys.
    /// </summary>
    public static RsaPssSigner CreateRsaSigner(RSA? rsa = null, string keyId = "test-rsa-key")
    {
        using var key = rsa is null ? RSA.Create(2048) : null;
        var source = rsa ?? key!;
        return new RsaPssSigner(new InMemorySigningKeyProvider(keyId, source.ExportPkcs8PrivateKey()));
    }
}

/// <summary>
/// In-memory single-version <see cref="ISigningKeyProvider"/> for tests. Returns the one held key for
/// its own id (and as the active key); any other id resolves to <c>null</c>.
/// </summary>
internal sealed class InMemorySigningKeyProvider : ISigningKeyProvider
{
    private readonly string _keyId;
    private readonly byte[] _material;

    public InMemorySigningKeyProvider(string keyId, byte[] material)
    {
        _keyId = keyId;
        _material = material;
    }

    public Task<(KeyDescriptor Descriptor, KeyMaterial Key)> GetActiveAsync(
        KeyScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult((Describe(), KeyMaterial.CopyOf(_material)));

    public Task<KeyMaterial?> GetByIdAsync(
        string keyId, KeyScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.Equals(keyId, _keyId, StringComparison.Ordinal)
            ? KeyMaterial.CopyOf(_material)
            : (KeyMaterial?)null);

    public Task<IReadOnlyList<KeyDescriptor>> ListActiveAsync(
        KeyScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KeyDescriptor>>([Describe()]);

    public Task<KeyDescriptor> RotateAsync(KeyScope scope, CancellationToken cancellationToken = default) =>
        Task.FromResult(Describe());

    private KeyDescriptor Describe() =>
        new(_keyId, _keyId, KeyStatus.Active, DateTimeOffset.UnixEpoch, "test");
}
