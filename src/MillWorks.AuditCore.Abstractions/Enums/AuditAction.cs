namespace MillWorks.AuditCore.Abstractions.Enums;

/// <summary>
/// Actions that can be audited in the system.
/// </summary>
public enum AuditAction
{
    /// <summary>
    /// Created action indicates that a new item was created in the system.
    /// </summary>
    Created = 1,

    /// <summary>
    /// Updated action indicates that an existing item was modified in the system.
    /// </summary>
    Updated = 2,

    /// <summary>
    /// Deleted action indicates that an item was removed from the system.
    /// </summary>
    Deleted = 3,

    /// <summary>
    /// StatusChanged action indicates that the status of an item was changed.
    /// </summary>
    StatusChanged = 4,

    /// <summary>
    /// Assigned action indicates that an item was assigned to a user or team.
    /// </summary>
    Assigned = 5,

    /// <summary>
    /// Unassigned action indicates that an item was unassigned from a user or team.
    /// </summary>
    Unassigned = 6,

    /// <summary>
    /// Commented action indicates that a comment was added to an item.
    /// </summary>
    Commented = 7,

    /// <summary>
    /// Attached action indicates that a file or document was attached to an item.
    /// </summary>
    Attached = 8,

    /// <summary>
    /// Unknown action indicates that the action type is not recognized or is not defined in the system.
    /// </summary>
    Unknown = 9, // Represents an unknown or unrecognized action

    /// <summary>
    /// Imported action indicates that data was imported into the system, typically from an external source.
    /// </summary>
    Imported = 10, // Represents an imported action, typically used for bulk data operations

    /// <summary>
    /// Exported action indicates that data was exported from the system, typically for reporting or backup purposes.
    /// </summary>
    Exported = 11, // Represents an exported action, typically used for data extraction

    /// <summary>
    /// Viewed action indicates that an item was viewed or accessed in the system.
    /// </summary>
    Viewed = 12, // Represents an action where an item was viewed or accessed
}