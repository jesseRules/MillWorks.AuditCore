using MillWorks.AuditCore.Services.Database.Options;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public sealed class ResilienceOptionsTests
{
    [Test]
    public void Defaults_AreCorrect()
    {
        var options = new ResilienceOptions();

        Assert.That(options.EnableDeadLetterQueue, Is.True);
        Assert.That(options.DeadLetterProvider, Is.EqualTo(DeadLetterProvider.FileSystem));
        Assert.That(options.EnableBackgroundProcessor, Is.True);
        Assert.That(options.MaxRetries, Is.EqualTo(3));
        Assert.That(options.RetryDelaySeconds, Is.EqualTo(1));
        Assert.That(options.IncludeStackTraces, Is.False);
        Assert.That(options.ProcessedRetention, Is.EqualTo(TimeSpan.FromDays(7)));
        Assert.That(options.FileBasedMaxQueueSize, Is.EqualTo(1000));
        Assert.That(options.FailFastOnDlqUnavailable, Is.False);
        Assert.That(options.FileBasedHardCapacity, Is.EqualTo(5000));
    }

    [Test]
    public void FailFastOnDlqUnavailable_CanBeSet()
    {
        var options = new ResilienceOptions { FailFastOnDlqUnavailable = true };
        Assert.That(options.FailFastOnDlqUnavailable, Is.True);
    }

    [Test]
    public void FileBasedHardCapacity_CanBeDisabled()
    {
        var options = new ResilienceOptions { FileBasedHardCapacity = 0 };
        Assert.That(options.FileBasedHardCapacity, Is.Zero);
    }
}
