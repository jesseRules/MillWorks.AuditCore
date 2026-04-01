using System.Text.RegularExpressions;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Best-effort scrubbing of known sensitive patterns from free-text fields like error messages.
/// Replaces matches with [SANITIZED] and optionally truncates. This is a heuristic — it catches
/// common leakage vectors (connection strings, tokens, SSNs, SQL key values, emails) but does not
/// guarantee all sensitive content is removed. Deployments handling real PHI should register a
/// custom <see cref="Abstractions.Interfaces.IAuditFieldRedactor"/> for full control.
/// </summary>
internal static class SensitiveContentSanitizer
{
    private static readonly Regex[] SensitivePatterns =
    [
        // Connection strings (SQL Server, PostgreSQL, MySQL, generic)
        new(@"(?i)(server|data source|host|initial catalog|database|uid|pwd|password|user id|integrated security)\s*=\s*[^;""'\s]+", RegexOptions.Compiled),
        // Bearer tokens and API keys
        new(@"(?i)(bearer|token|api[_\-]?key|authorization)\s*[:=\s]\s*\S+", RegexOptions.Compiled),
        // Email addresses (can appear in constraint violations)
        new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled),
        // SSN patterns
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),
        // Quoted values in SQL errors (e.g., "The duplicate key value is ('sensitive')")
        new(@"(?i)(?:key value is|values? \()\s*\('?[^)]*'?\)", RegexOptions.Compiled),
    ];

    /// <summary>
    /// Scrubs known sensitive patterns and optionally truncates.
    /// </summary>
    public static string Sanitize(string? value, int maxLength = 512)
    {
        if (string.IsNullOrEmpty(value))
            return value ?? string.Empty;

        var result = value;
        foreach (var pattern in SensitivePatterns)
            result = pattern.Replace(result, "[SANITIZED]");

        if (result.Length > maxLength)
            result = result[..maxLength] + "...[truncated]";

        return result;
    }
}
