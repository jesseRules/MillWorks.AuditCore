namespace MillWorks.AuditCore.Abstractions.Enums;

/// <summary>
/// Discriminator on <see cref="MillWorks.AuditCore.Abstractions.Models.AuditEnvelope"/>
/// identifying which producer path created the envelope and which optional fields are populated.
/// </summary>
public enum AuditEnvelopeKind
{
    /// <summary>
    /// Captured by AuditSaveChangesInterceptor from EF change-tracker entries.
    /// Carries property-level OldValue/NewValue diffs.
    /// </summary>
    EntityChange = 0,

    /// <summary>
    /// Explicit application-level event raised via IAuditLogger.LogAsync.
    /// Carries an EventType + AdditionalData payload.
    /// </summary>
    ExplicitEvent = 1,
}
