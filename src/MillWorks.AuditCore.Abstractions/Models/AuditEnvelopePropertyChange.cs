namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// Per-property change record carried on an <see cref="AuditEnvelope"/> when
/// <see cref="MillWorks.AuditCore.Abstractions.Enums.AuditEnvelopeKind.EntityChange"/>.
/// </summary>
/// <remarks>
/// <para>
/// Values are already masked / redacted by the producer (the interceptor or the
/// explicit caller) before the envelope is published. The sink does not re-mask.
/// </para>
/// </remarks>
/// <param name="PropertyName">Name of the property that changed.</param>
/// <param name="OldValue">Value before the change. May be null for Added entries
/// or when the prior value was redacted to null.</param>
/// <param name="NewValue">Value after the change. May be null for Deleted entries
/// or when the new value was redacted to null.</param>
public sealed record AuditEnvelopePropertyChange(
    string PropertyName,
    string? OldValue,
    string? NewValue);
