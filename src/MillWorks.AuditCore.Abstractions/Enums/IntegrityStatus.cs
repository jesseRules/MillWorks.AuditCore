namespace MillWorks.AuditCore.Abstractions.Enums;

/// <summary>
/// Tracks the integrity protection state of an audit event.
/// In strict mode, events are inserted as <see cref="Completed"/>.
/// In batched mode, events start as <see cref="Pending"/> and transition
/// to <see cref="Completed"/> after the background batcher creates the integrity record.
/// </summary>
public enum IntegrityStatus
{
    /// <summary>
    /// The audit event has been persisted but its integrity record has not yet been created.
    /// This is the initial state in batched mode.
    /// </summary>
    Pending = 1,

    /// <summary>
    /// The integrity record has been successfully created and linked to this audit event.
    /// This is the initial state in strict (immediate) mode.
    /// </summary>
    Completed = 2,

    /// <summary>
    /// The integrity record could not be created after repeated attempts.
    /// Requires operator investigation.
    /// </summary>
    Failed = 3,

    /// <summary>
    /// The integrity record was created by the reconciliation service after
    /// the original batched write failed or was lost.
    /// </summary>
    Reconciled = 4
}
