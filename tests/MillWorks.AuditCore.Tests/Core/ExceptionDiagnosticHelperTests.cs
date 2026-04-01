using FluentAssertions;
using MillWorks.AuditCore.Services;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class ExceptionDiagnosticHelperTests
{
    [Test]
    public void GetTruncatedMessage_SanitizesSensitiveContent()
    {
        var ex = new Exception("Login failed for Server=prod;Password=secret123");
        var result = ExceptionDiagnosticHelper.GetTruncatedMessage(ex);

        result.Should().NotContain("secret123");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void GetTruncatedMessage_PreservesSafeErrors()
    {
        var ex = new Exception("Timeout expired. The timeout period elapsed.");
        var result = ExceptionDiagnosticHelper.GetTruncatedMessage(ex);

        result.Should().Contain("Timeout expired");
    }

    [Test]
    public void GetTruncatedMessage_TruncatesLongMessages()
    {
        var ex = new Exception(new string('x', 500));
        var result = ExceptionDiagnosticHelper.GetTruncatedMessage(ex);

        result!.Length.Should().BeLessThanOrEqualTo(270); // 256 + "...[truncated]"
    }

    [Test]
    public void GetTruncatedMessage_NullException_ReturnsNull()
    {
        ExceptionDiagnosticHelper.GetTruncatedMessage(null).Should().BeNull();
    }

    [Test]
    public void GetTruncatedMessage_EmptyMessage_ReturnsEmpty()
    {
        var ex = new Exception("");
        ExceptionDiagnosticHelper.GetTruncatedMessage(ex).Should().BeEmpty();
    }

    [Test]
    public void GetStackTrace_WhenDisabled_ReturnsNull()
    {
        Exception ex;
        try { throw new InvalidOperationException("test"); }
        catch (Exception caught) { ex = caught; }

        var result = ExceptionDiagnosticHelper.GetStackTrace(ex, includeStackTraces: false);
        result.Should().BeNull();
    }

    [Test]
    public void GetStackTrace_WhenEnabled_ReturnsTrace()
    {
        Exception ex;
        try { throw new InvalidOperationException("test"); }
        catch (Exception caught) { ex = caught; }

        var result = ExceptionDiagnosticHelper.GetStackTrace(ex, includeStackTraces: true);
        result.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void GetExceptionType_ReturnsTypeName()
    {
        var ex = new InvalidOperationException("test");
        ExceptionDiagnosticHelper.GetExceptionType(ex).Should().Be("InvalidOperationException");
    }

    [Test]
    public void GetExceptionType_NullException_ReturnsNull()
    {
        ExceptionDiagnosticHelper.GetExceptionType(null).Should().BeNull();
    }
}
