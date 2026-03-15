namespace MillWorks.AuditCore.EntityFramework.Common;

/// <summary>
/// Interface for soft deletable entities
/// </summary>
public interface ISoftDeletable
{
    /// <summary>
    /// Indicates whether the entity is soft deleted
    /// </summary>
    bool IsDeleted { get; }

    /// <summary>
    /// Deletion timestamp
    /// </summary>
    DateTimeOffset? DeletedAt { get; }

    /// <summary>
    /// Deleter identifier
    /// </summary>
    Guid? DeletedById { get; }
}