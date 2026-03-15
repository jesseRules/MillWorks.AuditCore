namespace MillWorks.AuditCore.EntityFramework.Common;

/// <summary>
/// Interface for auditable entities
/// </summary>
public interface IAuditableEntity
{
    /// <summary>
    /// Creation timestamp
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Creator identifier
    /// </summary>
    Guid CreatedById { get; }

    /// <summary>
    /// Update timestamp
    /// </summary>
    DateTimeOffset? UpdatedAt { get; }

    /// <summary>
    /// Updater identifier
    /// </summary>
    Guid? UpdatedById { get; }
}