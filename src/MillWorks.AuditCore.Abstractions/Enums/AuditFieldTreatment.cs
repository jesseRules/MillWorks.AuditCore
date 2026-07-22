namespace MillWorks.AuditCore.Abstractions.Enums;

/// <summary>
/// How a single entity property should be treated when the EF change-capture interceptor
/// records it into the audit trail. Ordered from least to most protective so that a
/// "strictest wins" merge between attribute-derived defaults and a consumer
/// <see cref="Interfaces.IAuditPropertySensitivityPolicy"/> is a simple numeric maximum:
/// a policy may only tighten a property's treatment, never loosen it.
/// </summary>
public enum AuditFieldTreatment
{
    /// <summary>Capture the real value (default for unclassified properties).</summary>
    Audit = 0,

    /// <summary>Replace the value with a mask pattern (e.g. <c>***</c>). Maps to <c>[SensitiveData]</c>.</summary>
    Mask = 1,

    /// <summary>Replace the value with <c>[ENCRYPTED]</c>. Maps to <c>[EncryptedField]</c>.</summary>
    Encrypt = 2,

    /// <summary>Omit the property from the audit record entirely. Maps to <c>[NoAudit]</c>.</summary>
    Omit = 3
}
