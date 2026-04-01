using MillWorks.AuditCore.Services.Database.Options;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public sealed class SecurityOptionsTests
{
    [Test]
    public void Defaults_AreCorrect()
    {
        var options = new SecurityOptions();

        Assert.That(options.EnableTamperDetection, Is.True);
        Assert.That(options.UseRedisLocking, Is.False);
        Assert.That(options.EnableDigitalSignatures, Is.False);
        Assert.That(options.EnableBatchedIntegrityWrites, Is.False);
        Assert.That(options.IntegrityBatchSize, Is.EqualTo(50));
        Assert.That(options.IntegrityFlushInterval, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
    }

    [Test]
    public void BatchedIntegrityWrites_CanBeConfigured()
    {
        var options = new SecurityOptions
        {
            EnableBatchedIntegrityWrites = true,
            IntegrityBatchSize = 100,
            IntegrityFlushInterval = TimeSpan.FromSeconds(1)
        };

        Assert.That(options.EnableBatchedIntegrityWrites, Is.True);
        Assert.That(options.IntegrityBatchSize, Is.EqualTo(100));
        Assert.That(options.IntegrityFlushInterval, Is.EqualTo(TimeSpan.FromSeconds(1)));
    }
}
