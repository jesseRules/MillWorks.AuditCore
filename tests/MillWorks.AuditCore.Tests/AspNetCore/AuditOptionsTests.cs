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

    // The integrity HMAC key no longer lives on AuditOptions (it resolves via the integrity
    // ISigningKeyProvider), so EnableDigitalSignatures no longer requires an HmacKey on these options.

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
            EnableDigitalSignatures = true
        };
        options.DefaultCustomFields["tenant"] = "north";

        Assert.DoesNotThrow(() => options.Validate());
    }

    [Test]
    public void DefaultCustomFields_NullAssignment_CoercesToEmpty()
    {
        var options = new AuditOptions();

        options.DefaultCustomFields = null!;

        Assert.That(options.DefaultCustomFields, Is.Not.Null);
        Assert.That(options.DefaultCustomFields, Is.Empty);
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
