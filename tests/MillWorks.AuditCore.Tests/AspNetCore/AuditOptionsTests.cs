using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Services.Options;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Unit")]
public sealed class AuditOptionsTests
{
    [Test]
    public void ApplicationName_SanitizesUnsupportedCharacters()
    {
        var options = new AuditOptions();

        options.ApplicationName = " My<App>@Name! ";

        Assert.That(options.ApplicationName, Is.EqualTo("MyAppName"));
    }

    [Test]
    public void ApplicationName_WhenBlank_Throws()
    {
        var options = new AuditOptions();

        var ex = Assert.Throws<ArgumentException>(() => options.ApplicationName = " ");

        Assert.That(ex!.ParamName, Is.EqualTo("value"));
    }

    [Test]
    public void Environment_WhenTooLong_Throws()
    {
        var options = new AuditOptions();

        Assert.Throws<ArgumentException>(() => options.Environment = new string('a', 51));
    }

    [Test]
    public void Validate_WhenDigitalSignaturesEnabledWithoutKey_Throws()
    {
        var options = new AuditOptions
        {
            EnableDigitalSignatures = true,
            HmacKey = null
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Test]
    public void Validate_WhenDigitalSignaturesEnabledWithShortKey_Throws()
    {
        var options = new AuditOptions
        {
            EnableDigitalSignatures = true,
            HmacKey = "short-key"
        };

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Test]
    public void Validate_WhenTooManyDefaultCustomFields_Throws()
    {
        var options = new AuditOptions();
        for (var i = 0; i < 51; i++)
        {
            options.DefaultCustomFields[$"field-{i}"] = i;
        }

        Assert.Throws<InvalidOperationException>(() => options.Validate());
    }

    [Test]
    public void Validate_WithValidConfiguration_DoesNotThrow()
    {
        var options = new AuditOptions
        {
            EnableDigitalSignatures = true,
            HmacKey = "12345678901234567890123456789012"
        };
        options.DefaultCustomFields["tenant"] = "north";

        Assert.DoesNotThrow(() => options.Validate());
    }

    [Test]
    public void FailureMode_DefaultsToPermissive()
    {
        var options = new AuditOptions();

        Assert.That(options.FailureMode, Is.EqualTo(AuditFailureMode.Permissive));
    }

    [Test]
    public void FailureMode_CanBeConfigured()
    {
        var options = new AuditOptions
        {
            FailureMode = AuditFailureMode.FailClosedForRegulated
        };

        Assert.That(options.FailureMode, Is.EqualTo(AuditFailureMode.FailClosedForRegulated));

        options.FailureMode = AuditFailureMode.FailClosedAlways;

        Assert.That(options.FailureMode, Is.EqualTo(AuditFailureMode.FailClosedAlways));
    }
}
