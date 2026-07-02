using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.Cryptography.Aead;
using MillWorks.Cryptography.KeyManagement;
using MillWorks.Cryptography.Signing;

namespace MillWorks.AuditCore.Tests.AspNetCore;

/// <summary>
/// Key-usage isolation guard (Cryptography consolidation A1, §7 cross-cutting rule). Asserts that the
/// audit integrity signing keys resolve ONLY through <see cref="ISigningKeyProvider"/>, that the HMAC
/// and RSA-PSS integrity keys live in disjoint key spaces, and that <see cref="TamperDetectionService"/>
/// never takes — and therefore can never cross-route through — an encryption-key dependency.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class IntegrityKeyUsageIsolationTests
{
    private static ServiceProvider BuildAuditProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // No IHostEnvironment registered → non-Production → the file key backend uses a process-ephemeral
        // master key and a temporary key store (it auto-generates the integrity signing keys on first use).
        services.AddMillWorksAudit(static builder =>
        {
            builder.Options.Environment = "Development";
            builder.Options.EnableDigitalSignatures = true;
            builder.UseEntityFramework(static ef => { ef.ConnectionString = "Server=test;Database=test;"; });
            builder.UseSecurity(static _ => { });
        });
        return services.BuildServiceProvider();
    }

    [Test]
    public async Task IntegrityHmacAndRsaKeys_ResolveFromDisjointKeySpaces()
    {
        using var provider = BuildAuditProvider();

        var hmacSigner = provider.GetRequiredService<HmacSha256Signer>();
        var rsaSigner = provider.GetRequiredService<RsaPssSigner>();

        var data = Encoding.UTF8.GetBytes("integrity-chain-binding");
        var hmacEnvelope = await hmacSigner.SignAsync(data, KeyScope.Global);
        var rsaEnvelope = await rsaSigner.SignAsync(data, KeyScope.Global);

        // Disjoint key spaces: the two integrity keys are different keys (distinct ids/algorithms).
        Assert.That(hmacEnvelope.Alg, Is.EqualTo(SignatureAlgorithm.HmacSha256));
        Assert.That(rsaEnvelope.Alg, Is.EqualTo(SignatureAlgorithm.RsaPssSha256));
        Assert.That(hmacEnvelope.KeyId, Is.Not.EqualTo(rsaEnvelope.KeyId),
            "The HMAC and RSA-PSS integrity keys must come from disjoint key spaces.");

        // Neither signer accepts the other's envelope — the keys are not cross-routable.
        Assert.That(await rsaSigner.VerifyAsync(data, hmacEnvelope, KeyScope.Global), Is.False);
        Assert.That(await hmacSigner.VerifyAsync(data, rsaEnvelope, KeyScope.Global), Is.False);
    }

    [Test]
    public void TamperDetectionService_TakesNoEncryptionKeyDependency()
    {
        var ctorParameters = typeof(TamperDetectionService)
            .GetConstructors()
            .Single()
            .GetParameters();

        // Integrity keys resolve only via signing providers — never an encryption-key provider or AEAD
        // cipher — so the service cannot cross-route a content-encryption key into the chain.
        Assert.That(
            ctorParameters.Any(p => p.ParameterType == typeof(IEncryptionKeyProvider)),
            Is.False,
            "TamperDetectionService must not depend on IEncryptionKeyProvider.");
        Assert.That(
            ctorParameters.Any(p => p.ParameterType == typeof(IAeadCipher)),
            Is.False,
            "TamperDetectionService must not depend on the AEAD cipher.");
        Assert.That(
            ctorParameters.Any(p => p.ParameterType.Namespace == "MillWorks.Cryptography.Aead"),
            Is.False,
            "TamperDetectionService must take no encryption (AEAD) dependency.");
    }
}
