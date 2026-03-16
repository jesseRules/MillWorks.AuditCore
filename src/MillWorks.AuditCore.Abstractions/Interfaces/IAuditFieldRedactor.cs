namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Redacts sensitive fields from audit event data before persistence.
/// Implement this interface to strip or mask PHI, PII, credentials, or other
/// sensitive data that should not appear in plaintext in the audit database.
///
/// The default implementation is a pass-through that performs no redaction.
/// Register a custom implementation before calling AddMillWorksAudit() to override.
/// </summary>
public interface IAuditFieldRedactor
{
    /// <summary>
    /// Redacts sensitive values from a dictionary of audit fields.
    /// Called before serialization to JsonData and AdditionalData.
    /// </summary>
    /// <param name="fields">The fields to redact. Implementations should return a new dictionary
    /// or modify and return the input — either approach is acceptable.</param>
    /// <returns>The redacted fields dictionary.</returns>
    Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields);

    /// <summary>
    /// Redacts a single string value that will be stored in a named column.
    /// Called for values mapped to dedicated entity columns (e.g., IpAddress, UserAgent).
    /// Return null to suppress the value entirely, or return a masked version.
    /// </summary>
    /// <param name="fieldName">The name of the field being stored.</param>
    /// <param name="value">The current value.</param>
    /// <returns>The redacted value, or null to suppress.</returns>
    string? RedactValue(string fieldName, string? value);
}
