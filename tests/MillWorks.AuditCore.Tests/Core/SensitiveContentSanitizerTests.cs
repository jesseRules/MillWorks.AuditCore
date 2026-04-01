using FluentAssertions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class SensitiveContentSanitizerTests
{
    [Test]
    public void Sanitize_ConnectionString_RemovesCredentials()
    {
        var input = "Login failed. Server=prod.db.com;User Id=admin;Password=hunter2;";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("hunter2");
        result.Should().NotContain("admin");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_EmailInConstraintViolation_RemovesEmail()
    {
        var input = "Duplicate key: patient@hospital.org already exists";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("patient@hospital.org");
    }

    [Test]
    public void Sanitize_SSN_RemovesPattern()
    {
        var input = "Validation failed for 123-45-6789";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("123-45-6789");
    }

    [Test]
    public void Sanitize_BearerToken_Removed()
    {
        var input = "Auth failed. Bearer eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.payload.sig";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9");
    }

    [Test]
    public void Sanitize_SqlDuplicateKeyValue_Removed()
    {
        var input = "Violation of PRIMARY KEY constraint. The duplicate key value is ('john.doe@example.com').";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("john.doe@example.com");
    }

    [Test]
    public void Sanitize_SafeContent_PreservesMessage()
    {
        var input = "Connection timeout after 30 seconds";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Be("Connection timeout after 30 seconds");
    }

    [Test]
    public void Sanitize_Null_ReturnsEmpty()
    {
        SensitiveContentSanitizer.Sanitize(null).Should().BeEmpty();
    }

    [Test]
    public void Sanitize_ExceedsMaxLength_Truncates()
    {
        var input = new string('a', 1000);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 100);

        result.Length.Should().BeLessThanOrEqualTo(115); // 100 + "...[truncated]"
    }
}
