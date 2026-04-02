using System.Text.RegularExpressions;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Best-effort scrubbing of known sensitive patterns from free-text fields like error messages.
/// Replaces matches with [SANITIZED] and optionally truncates. This is a heuristic — it catches
/// common leakage vectors (connection strings, tokens, SSNs, credit cards, phone numbers, SQL key
/// values, emails) but does not guarantee all sensitive content is removed. Deployments handling
/// real PHI should register a custom <see cref="Abstractions.Interfaces.IAuditFieldRedactor"/>
/// for full control.
/// </summary>
internal static class SensitiveContentSanitizer
{
    private const string TruncationSuffix = "...[truncated]";

    private static readonly Regex[] SensitivePatterns =
    [
        // Connection strings (SQL Server, PostgreSQL, MySQL, generic)
        new(@"(?i)(server|data source|host|initial catalog|database|uid|pwd|password|user id|integrated security)\s*=\s*[^;""'\s]+", RegexOptions.Compiled),
        // Bearer tokens and API keys
        new(@"(?i)(bearer|token|api[_\-]?key|authorization)\s*[:=\s]\s*\S+", RegexOptions.Compiled),
        // Email addresses (can appear in constraint violations)
        new(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled),
        // SSN patterns (with hyphens)
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),
        // SSN patterns (without hyphens) — SSA validation rules reduce false positives:
        // area ≠ 000/666/9xx, group ≠ 00, serial ≠ 0000
        new(@"\b(?!000|666|9\d\d)\d{3}(?!00)\d{2}(?!0000)\d{4}\b", RegexOptions.Compiled),
        // Credit card numbers: Visa (4xxx), Mastercard (51-55xx), Amex (34/37xx), Discover (6011/65xx)
        // Handles optional space/dash separators between 4-digit groups
        new(@"\b(?:4\d{3}|5[1-5]\d{2}|3[47]\d{2}|6(?:011|5\d{2}))[\s\-]?\d{4}[\s\-]?\d{4}[\s\-]?\d{1,4}\b", RegexOptions.Compiled),
        // US/NANP phone numbers: (xxx) xxx-xxxx, xxx-xxx-xxxx, xxx.xxx.xxxx, +1-xxx-xxx-xxxx
        // Area code and exchange first digit must be 2-9 per NANP rules
        new(@"(?<!\d)(?:\+?1[\-.\s]?)?\(?[2-9]\d{2}\)?[\-.\s]\s?[2-9]\d{2}[\-.\s]\d{4}(?!\d)", RegexOptions.Compiled),
        // Quoted/parenthesized values in SQL errors (e.g., "The duplicate key value is ('sensitive')")
        // Handles both "key value is (...)" and "values(...)" with optional whitespace
        new(@"(?i)(?:key\s+value\s+is|values?\s*\()\s*\(?'?[^)]*'?\)", RegexOptions.Compiled),
        // SQL INSERT statements: INSERT INTO table (...) VALUES (...)
        new(@"(?i)insert\s+into\s+\S+\s*(?:\([^)]*\)\s*)?values\s*\([^)]*\)", RegexOptions.Compiled),
        // GUID/UUID values in constraint violation messages
        new(@"(?i)(?:key|constraint|violation|duplicate|conflict)[^.]*\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", RegexOptions.Compiled),
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
        {
            if (maxLength > TruncationSuffix.Length)
                result = result[..(maxLength - TruncationSuffix.Length)] + TruncationSuffix;
            else
                result = result[..maxLength];
        }

        return result;
    }
}
