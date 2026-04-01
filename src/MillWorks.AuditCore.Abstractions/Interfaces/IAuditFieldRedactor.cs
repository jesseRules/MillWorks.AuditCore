using MillWorks.AuditCore.Abstractions.Models;

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
    /// <returns>The redacted fields' dictionary.</returns>
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

    /// <summary>
    /// Redacts sensitive data from an audit target before serialization into JsonData.
    /// Called for the <see cref="MillWorks.AuditCore.Abstractions.Models.AuditTarget"/> object
    /// which may contain entity snapshots with PHI/PII (e.g., via SetTarget(patientEntity)).
    /// The default implementation returns the target unchanged.
    /// </summary>
    /// <param name="target">The audit target to redact, or null.</param>
    /// <returns>The redacted audit target, or null to suppress entirely.</returns>
    AuditTarget? RedactTarget(AuditTarget? target) => target;

    /// <summary>
    /// Filters a list of changed property names, redacting those that reveal sensitive schema
    /// information (e.g., "SSN", "Diagnosis"). Default implementation returns names unchanged.
    /// </summary>
    List<string>? RedactPropertyNames(List<string>? propertyNames) => propertyNames;

    /// <summary>
    /// Redacts sensitive values within entity key-value pairs. Natural keys containing
    /// PHI (emails, SSNs) should be masked while surrogate keys (ints, GUIDs) pass through.
    /// Default implementation returns key values unchanged.
    /// </summary>
    Dictionary<string, object?>? RedactKeyValues(Dictionary<string, object?>? keyValues)
        => keyValues;
}
