using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Conservative default redactor that masks all field values. This is the safe-by-default
/// posture: no PHI/PII can leak to the audit store unless the consumer explicitly registers
/// a custom <see cref="IAuditFieldRedactor"/> that selectively allows fields through.
///
/// Consumers should register their own redactor before calling AddMillWorksAudit() or
/// use the builder's <c>UseRedactor&lt;T&gt;()</c> method to replace this default.
/// </summary>
public sealed class DefaultAuditFieldRedactor : IAuditFieldRedactor
{
    /// <summary>
    /// Redaction mask applied to all field values.
    /// </summary>
    public const string RedactionMask = "[REDACTED]";

    /// <summary>
    /// Fields that are safe to pass through without redaction.
    /// These are structural/operational fields that never contain PHI/PII.
    /// </summary>
    private static readonly HashSet<string> SafeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "EventType",
        "OperationType",
        "Status",
        "Action",
        "Environment",
        "MachineName",
        "AssemblyName",
        "CallingMethodName",
        "CorrelationId",
        "Duration",
        "Success",
        "ErrorMessage",
        "RequestMethod",
        "_FerpaEventType",
        "_ConsentRequired",
        "_RecordType",
        "_serializationError",
        "_entityName",
        "_action"
        // "_propertyNames" intentionally excluded — property names can reveal
        // sensitive metadata (e.g., column names like "Diagnosis" or "SSN")
    };

    /// <inheritdoc />
    public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields)
    {
        var redacted = new Dictionary<string, object?>(fields.Count);
        foreach (var (key, value) in fields)
        {
            redacted[key] = SafeFields.Contains(key) ? value : RedactionMask;
        }
        return redacted;
    }

    /// <inheritdoc />
    public string? RedactValue(string fieldName, string? value)
    {
        if (value is null) return null;
        return SafeFields.Contains(fieldName) ? value : RedactionMask;
    }

    /// <inheritdoc />
    public AuditTarget? RedactTarget(AuditTarget? target)
    {
        if (target is null) return null;

        // Redact the snapshot data while preserving structural metadata
        return new AuditTarget
        {
            Type = target.Type,
            Old = target.Old is not null ? new { _redacted = true } : null,
            New = target.New is not null ? new { _redacted = true } : null
        };
    }
}
