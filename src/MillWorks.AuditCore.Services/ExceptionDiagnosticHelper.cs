using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Services;

/// <summary>
/// Builds safe, truncated exception diagnostic fields for DLQ storage.
/// Full stack traces are suppressed by default to avoid leaking internal
/// implementation details, provider errors, and sensitive embedded values.
/// </summary>
internal static class ExceptionDiagnosticHelper
{
    private const int MaxMessageLength = 256;

    /// <summary>
    /// Returns the exception type name (e.g. "DbUpdateException").
    /// </summary>
    internal static string? GetExceptionType(Exception? exception)
        => exception?.GetType().Name;

    /// <summary>
    /// Returns a sanitized and truncated exception message. Known sensitive patterns
    /// (connection strings, tokens, SSNs, SQL key values) are scrubbed before truncation.
    /// </summary>
    internal static string? GetTruncatedMessage(Exception? exception)
    {
        var message = exception?.Message;
        if (string.IsNullOrEmpty(message)) return message;

        return SensitiveContentSanitizer.Sanitize(message, MaxMessageLength);
    }

    /// <summary>
    /// Returns the full stack trace only when <paramref name="includeStackTraces"/> is true.
    /// </summary>
    internal static string? GetStackTrace(Exception? exception, bool includeStackTraces)
        => includeStackTraces ? exception?.StackTrace : null;
}
