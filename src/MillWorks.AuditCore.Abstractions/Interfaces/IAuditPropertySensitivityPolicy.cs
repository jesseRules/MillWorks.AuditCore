using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Identifies a single entity property for sensitivity classification.
/// <paramref name="EntityType"/> is the concrete CLR entity type being audited (not the
/// declaring type), so a policy can classify members inherited from a framework base type —
/// e.g. <c>Email</c> declared on <c>IdentityUser&lt;Guid&gt;</c> but carried by a concrete
/// <c>ApplicationUser</c>.
/// </summary>
public readonly record struct AuditPropertyRef(Type EntityType, string PropertyName);

/// <summary>
/// Consumer-supplied classification consulted by the EF change-capture interceptor
/// (<c>AuditSaveChangesInterceptor</c>) <b>in addition to</b> AuditCore's own
/// <c>[SensitiveData]</c> / <c>[EncryptedField]</c> / <c>[NoAudit]</c> attributes.
/// <para>
/// This exists because attributes cannot cover every sensitive property: members declared on a
/// framework base type (such as ASP.NET Core Identity's <c>IdentityUser&lt;Guid&gt;</c>) cannot
/// be decorated by the consumer, yet the consumer may classify them through its own metadata
/// system (e.g. a class-level <c>[IdentityPii]</c> attribute). A policy maps that metadata onto
/// AuditCore's <see cref="AuditFieldTreatment"/> once.
/// </para>
/// <para>
/// Register zero or more implementations in DI (<c>AddSingleton&lt;IAuditPropertySensitivityPolicy, ...&gt;()</c>).
/// When none are registered the interceptor's behaviour is byte-for-byte unchanged. When one or
/// more are registered, the interceptor merges each policy's result with the attribute-derived
/// default using <b>strictest wins</b> — a policy can tighten a treatment
/// (<c>Audit → Mask → Encrypt → Omit</c>) but never loosen it.
/// </para>
/// <para>
/// Implementations MUST be pure and deterministic for a given <see cref="AuditPropertyRef"/>: the
/// interceptor caches the merged result per <c>(EntityType, PropertyInfo)</c> for the lifetime of
/// the singleton interceptor.
/// </para>
/// </summary>
public interface IAuditPropertySensitivityPolicy
{
    /// <summary>
    /// Returns the treatment this policy wants applied to <paramref name="property"/>, or
    /// <see langword="null"/> to defer (no opinion — leave the attribute-derived default and other
    /// policies to decide).
    /// </summary>
    AuditFieldTreatment? Classify(in AuditPropertyRef property);

    /// <summary>
    /// Optional mask pattern to use when this policy's classification resolves to
    /// <see cref="AuditFieldTreatment.Mask"/>. Return <see langword="null"/> to fall back to the
    /// attribute-supplied pattern, then to the interceptor default (<c>***</c>).
    /// </summary>
    string? MaskPattern(in AuditPropertyRef property) => null;
}
