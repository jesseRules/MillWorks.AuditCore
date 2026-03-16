using MillWorks.AuditCore.Abstractions.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Default no-op redactor that passes all fields through unchanged.
/// Replace with a custom implementation to redact PHI/PII before persistence.
/// </summary>
public sealed class PassThroughAuditFieldRedactor : IAuditFieldRedactor
{
    /// <inheritdoc />
    public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields) => fields;

    /// <inheritdoc />
    public string? RedactValue(string fieldName, string? value) => value;
}
