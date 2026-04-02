using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Services.Encryption.Providers;

namespace MillWorks.AuditCore.Tests.Services.Encryption;

[TestFixture]
[Category("Unit")]
public class AzureKeyVaultProviderTests
{
    private Mock<SecretClient> _mockSecretClient = null!;
    private Mock<ILogger<AzureKeyVaultProvider>> _mockLogger = null!;
    private FakeTimeProvider _timeProvider = null!;
    private AzureKeyVaultProvider _provider = null!;
    private string _masterKeyBase64 = null!;

    [SetUp]
    public void Setup()
    {
        _mockSecretClient = new Mock<SecretClient>(MockBehavior.Strict, new Uri("https://unit-test.vault.azure.net/"), new Mock<TokenCredential>().Object);
        _mockLogger = new Mock<ILogger<AzureKeyVaultProvider>>();
        _timeProvider = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var masterKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        _masterKeyBase64 = Convert.ToBase64String(masterKey);

        _provider = new AzureKeyVaultProvider(
            _mockSecretClient.Object,
            _mockLogger.Object,
            _timeProvider,
            TimeSpan.FromMinutes(5));
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    [Test]
    public async Task GetEncryptionKeyAsync_ReturnsDerivedKeyForKnownSecret()
    {
        SetupCurrentVersion("v1");
        SetupKeySecret("v1", _masterKeyBase64);

        var key = await _provider.GetEncryptionKeyAsync("PatientSsn");

        Assert.That(key, Has.Length.EqualTo(32));
        Assert.That(key, Is.Not.EqualTo(Convert.FromBase64String(_masterKeyBase64)));
    }

    [Test]
    public async Task GetEncryptionKeyAsync_CachesKeyAndVersion_AvoidsSecondVaultRead()
    {
        SetupCurrentVersion("v1");
        SetupKeySecret("v1", _masterKeyBase64);

        var first = await _provider.GetEncryptionKeyAsync("PatientSsn");
        var second = await _provider.GetEncryptionKeyAsync("PatientSsn");

        Assert.That(second, Is.EqualTo(first));
        _mockSecretClient.Verify(
            x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSecretClient.Verify(
            x => x.GetSecretAsync("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task GetEncryptionKeyAsync_AfterCacheExpiry_RefreshesVersionAndKey()
    {
        SetupCurrentVersionSequence("v1", "v2");
        SetupKeySecret("v1", _masterKeyBase64);
        SetupKeySecret("v2", Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray()));

        var first = await _provider.GetEncryptionKeyAsync("PatientSsn");
        _timeProvider.Advance(TimeSpan.FromMinutes(6));
        var second = await _provider.GetEncryptionKeyAsync("PatientSsn");

        Assert.That(second, Is.Not.EqualTo(first));
        _mockSecretClient.Verify(
            x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Test]
    public void Constructor_WithMissingVaultUri_ThrowsArgumentException()
    {
        Assert.Throws<ArgumentException>(() => new AzureKeyVaultProvider("", _mockLogger.Object));
    }

    [Test]
    public void GetEncryptionKeyAsync_WhenSecretMissing_ThrowsKeyProviderExceptionWithMeaningfulMessage()
    {
        SetupCurrentVersion("v1");
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(404, "missing"));

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetEncryptionKeyAsync("PatientSsn"));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public void GetEncryptionKeyAsync_WhenSecretEmpty_ThrowsKeyProviderException()
    {
        SetupCurrentVersion("v1");
        SetupKeySecret("v1", "");

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetEncryptionKeyAsync("PatientSsn"));

        Assert.That(ex!.Message, Does.Contain("empty"));
    }

    [Test]
    public void GetEncryptionKeyAsync_WhenAccessDenied_ThrowsDistinctKeyProviderException()
    {
        SetupCurrentVersion("v1");
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden"));

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetEncryptionKeyAsync("PatientSsn"));

        Assert.That(ex!.Message, Does.Contain("Access denied"));
    }

    [Test]
    public void GetEncryptionKeyAsync_WhenVaultUnavailable_WrapsOriginalException()
    {
        SetupCurrentVersion("v1");
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("network timeout"));

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetEncryptionKeyAsync("PatientSsn"));

        Assert.That(ex!.InnerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task GetEncryptionKeyAsync_ConcurrentCallsForSameKey_OnlyHitsVaultOncePerSecret()
    {
        var versionCalls = 0;
        var keyCalls = 0;

        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref versionCalls);
                await Task.Delay(50);
                return CreateSecretResponse("audit-encryption-current-version", "v1");
            });

        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                Interlocked.Increment(ref keyCalls);
                await Task.Delay(50);
                return CreateSecretResponse("audit-encryption-key-v1", _masterKeyBase64);
            });

        var tasks = Enumerable.Range(0, 5)
            .Select(_ => _provider.GetEncryptionKeyAsync("PatientSsn"))
            .ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.That(results.All(r => r.SequenceEqual(results[0])), Is.True);
        Assert.That(versionCalls, Is.EqualTo(1));
        Assert.That(keyCalls, Is.EqualTo(1));
    }

    [Test]
    public void DisposedProvider_ThrowsObjectDisposedException()
    {
        _provider.Dispose();

        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await _provider.GetEncryptionKeyAsync("PatientSsn"));
    }

    [Test]
    public async Task GetEncryptionKeyAsync_DoesNotLogKeyMaterial()
    {
        SetupCurrentVersion("v1");
        SetupKeySecret("v1", _masterKeyBase64);

        await _provider.GetEncryptionKeyAsync("PatientSsn");

        Assert.That(
            _mockLogger.Invocations.Any(i =>
                i.Arguments.Any(a => a?.ToString()?.Contains(_masterKeyBase64, StringComparison.Ordinal) == true)),
            Is.False);
    }

    [Test]
    public void GetEncryptionKey_SyncPath_CachesVersionAndKey()
    {
        SetupCurrentVersionSync("v1");
        SetupKeySecretSync("v1", _masterKeyBase64);

        var first = _provider.GetEncryptionKey("PatientSsn");
        var second = _provider.GetEncryptionKey("PatientSsn");

        Assert.That(second, Is.EqualTo(first));
        _mockSecretClient.Verify(
            x => x.GetSecret("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSecretClient.Verify(
            x => x.GetSecret("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public void GetEncryptionKey_SyncPath_WhenSecretMissing_ThrowsKeyProviderException()
    {
        SetupCurrentVersionSync("v1");
        _mockSecretClient
            .Setup(x => x.GetSecret("audit-encryption-key-v1", null, null, It.IsAny<CancellationToken>()))
            .Throws(new RequestFailedException(404, "missing"));

        var ex = Assert.Throws<KeyProviderException>(() => _provider.GetEncryptionKey("PatientSsn"));

        Assert.That(ex!.Message, Does.Contain("not found"));
    }

    [Test]
    public void GetCurrentKeyVersion_SyncPath_WhenSecretEmpty_ThrowsKeyProviderException()
    {
        SetupCurrentVersionSync("");

        var ex = Assert.Throws<KeyProviderException>(() => _provider.GetCurrentKeyVersion());

        Assert.That(ex!.Message, Does.Contain("empty"));
    }

    [Test]
    public void GetCurrentKeyVersionAsync_WhenAccessDenied_ThrowsDistinctKeyProviderException()
    {
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException(403, "forbidden"));

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetCurrentKeyVersionAsync());

        Assert.That(ex!.Message, Does.Contain("Access denied"));
    }

    [Test]
    public void GetCurrentKeyVersionAsync_WhenVaultUnavailable_WrapsOriginalException()
    {
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("network timeout"));

        var ex = Assert.ThrowsAsync<KeyProviderException>(async () =>
            await _provider.GetCurrentKeyVersionAsync());

        Assert.That(ex!.InnerException, Is.TypeOf<TimeoutException>());
    }

    [Test]
    public async Task RotateKeysAsync_StoresNewKeyAndCurrentVersion()
    {
        string? currentVersion = null;

        _mockSecretClient
            .Setup(x => x.SetSecretAsync(It.Is<string>(name => name.StartsWith("audit-encryption-key-v")), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, string value, CancellationToken _) =>
            {
                Assert.That(value, Is.Not.Empty);
                currentVersion = name.Replace("audit-encryption-key-", string.Empty);
                return CreateSecretResponse(name, value);
            });

        _mockSecretClient
            .Setup(x => x.SetSecretAsync("audit-encryption-current-version", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, string value, CancellationToken _) =>
            {
                Assert.That(value, Is.EqualTo(currentVersion));
                return CreateSecretResponse(name, value);
            });

        var version = await _provider.RotateKeysAsync();

        Assert.That(version, Does.StartWith("v"));
        _mockSecretClient.Verify(
            x => x.SetSecretAsync(It.Is<string>(name => name.StartsWith("audit-encryption-key-v")), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _mockSecretClient.Verify(
            x => x.SetSecretAsync("audit-encryption-current-version", version, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    private void SetupCurrentVersion(string version)
    {
        _mockSecretClient
            .Setup(x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSecretResponse("audit-encryption-current-version", version));
    }

    private void SetupCurrentVersionSync(string version)
    {
        _mockSecretClient
            .Setup(x => x.GetSecret("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateSecretResponse("audit-encryption-current-version", version));
    }

    private void SetupCurrentVersionSequence(params string[] versions)
    {
        var sequence = _mockSecretClient
            .SetupSequence(x => x.GetSecretAsync("audit-encryption-current-version", null, null, It.IsAny<CancellationToken>()));

        foreach (var version in versions)
        {
            sequence = sequence.ReturnsAsync(CreateSecretResponse("audit-encryption-current-version", version));
        }
    }

    private void SetupKeySecret(string version, string secretValue)
    {
        _mockSecretClient
            .Setup(x => x.GetSecretAsync($"audit-encryption-key-{version}", null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateSecretResponse($"audit-encryption-key-{version}", secretValue));
    }

    private void SetupKeySecretSync(string version, string secretValue)
    {
        _mockSecretClient
            .Setup(x => x.GetSecret($"audit-encryption-key-{version}", null, null, It.IsAny<CancellationToken>()))
            .Returns(CreateSecretResponse($"audit-encryption-key-{version}", secretValue));
    }

    private static Response<KeyVaultSecret> CreateSecretResponse(string name, string value)
    {
        return Response.FromValue(new KeyVaultSecret(name, value), Mock.Of<Response>());
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }
}
