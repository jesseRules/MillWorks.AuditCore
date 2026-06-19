using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Options;

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
    /// Base fields that are always safe to pass through without redaction.
    /// These are structural/operational fields that never contain PHI/PII.
    /// </summary>
    private static readonly HashSet<string> _baseSafeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "EventType",
        "OperationType",
        "Status",
        "Action",
        "Environment",
        "MachineName",
        "AssemblyName",
        "CallingMethodName",
        // CorrelationId intentionally excluded — many systems embed user IDs, emails, or
        // other PII into correlation IDs. Can be added via RedactionOptions.AdditionalSafeFields.
        // SessionId intentionally excluded — same rationale as CorrelationId.
        "Duration",
        "Success",
        // ErrorMessage intentionally excluded — it carries arbitrary runtime content
        // (SQL errors, API responses) that can embed connection strings, tokens, or PHI.
        // Routed through SensitiveContentSanitizer instead.
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

    /// <summary>
    /// Set of field names that are allowed to pass through without redaction. Initialized with the base safe fields and can be extended via constructor options.
    /// </summary>
    private readonly HashSet<string> _safeFields;

    /// <summary>
    /// Creates a new instance with default safe fields only.
    /// </summary>
    public DefaultAuditFieldRedactor() : this(null) { }

    /// <summary>
    /// Creates a new instance with configurable additional safe fields.
    /// </summary>
    /// <param name="options">Optional configuration for additional safe fields.</param>
    public DefaultAuditFieldRedactor(IOptions<RedactionOptions>? options)
    {
        _safeFields = new HashSet<string>(_baseSafeFields, StringComparer.OrdinalIgnoreCase);

        var additionalFields = options?.Value.AdditionalSafeFields;
        if (additionalFields is { Length: > 0 })
        {
            foreach (var field in additionalFields)
            {
                _safeFields.Add(field);
            }
        }
    }

    /// <inheritdoc />
    public Dictionary<string, object?> RedactFields(Dictionary<string, object?> fields)
    {
        var redacted = new Dictionary<string, object?>(fields.Count);
        foreach (var (key, value) in fields)
        {
            if (string.Equals(key, "ErrorMessage", StringComparison.OrdinalIgnoreCase))
            {
                redacted[key] = SensitiveContentSanitizer.Sanitize(value?.ToString());
                continue;
            }

            redacted[key] = _safeFields.Contains(key) ? value : RedactionMask;
        }
        return redacted;
    }

    /// <inheritdoc />
    public string? RedactValue(string fieldName, string? value)
    {
        if (value is null) return null;
        if (string.Equals(fieldName, "ErrorMessage", StringComparison.OrdinalIgnoreCase))
            return SensitiveContentSanitizer.Sanitize(value);
        return _safeFields.Contains(fieldName) ? value : RedactionMask;
    }

    /// <summary>
    /// Property names that reveal sensitive schema metadata in healthcare, FERPA, and auth contexts.
    /// </summary>
    private static readonly HashSet<string> _sensitivePropertyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // Healthcare / PHI
        "SSN", "SocialSecurityNumber", "DateOfBirth", "DOB", "Diagnosis", "DiagnosisCode",
        "MedicalRecordNumber", "MRN", "PatientId", "InsuranceNumber", "InsurancePolicyNumber",
        // Auth / secrets
        "Password", "PasswordHash", "PasswordSalt", "Secret", "SecretKey",
        "Token", "RefreshToken", "ApiKey", "AccessToken",
        // Financial / PII
        "CreditCardNumber", "AccountNumber", "RoutingNumber", "TaxId", "EIN",
        // FERPA
        "StudentId", "GradePointAverage", "GPA", "TranscriptId",
    };

    /// <inheritdoc />
    public List<string>? RedactPropertyNames(List<string>? propertyNames)
    {
        if (propertyNames is null or { Count: 0 })
            return propertyNames;

        return propertyNames
            .Select(static name => _sensitivePropertyNames.Contains(name)
                ? "[REDACTED_PROP]"
                : name)
            .ToList();
    }

    /// <inheritdoc />
    public Dictionary<string, object?>? RedactKeyValues(Dictionary<string, object?>? keyValues)
    {
        if (keyValues is null or { Count: 0 })
            return keyValues;

        var result = new Dictionary<string, object?>(keyValues.Count);
        foreach (var (key, value) in keyValues)
        {
            // Preserve null, numeric types, and GUIDs (surrogate keys are non-sensitive)
            if (value is null or int or long or short or byte or Guid or decimal or double or float)
            {
                result[key] = value;
                continue;
            }

            // String values could be natural keys (email, SSN, etc.) — redact
            result[key] = RedactionMask;
        }

        return result;
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
