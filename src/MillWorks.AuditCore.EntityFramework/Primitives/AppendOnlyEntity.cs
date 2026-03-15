namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Base class for immutable, append-only entities that are never updated or soft-deleted.
/// Provides only Id (from AuditEntity), CreatedAt, and CreatedById.
/// </summary>
public abstract class AppendOnlyEntity : AuditEntity
{
    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Creator user ID
    /// </summary>
    public Guid CreatedById { get; set; }

    /// <summary>
    /// Default constructor
    /// </summary>
    protected AppendOnlyEntity() : base()
    {
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Constructor with specified ID
    /// </summary>
    protected AppendOnlyEntity(Guid id) : base(id)
    {
        CreatedAt = DateTimeOffset.UtcNow;
    }
}
