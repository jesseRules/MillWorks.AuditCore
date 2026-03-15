namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Entity deleted event
/// </summary>
/// <param name="entityId"></param>
/// <param name="entityType"></param>
public sealed class AuditEntityDeletedEvent(Guid entityId, string entityType) : AuditDomainEvent
{
    /// <summary>
    /// Entity identifier
    /// </summary>
    public Guid EntityId { get; } = entityId;

    /// <summary>
    /// Entity type
    /// </summary>
    public string EntityType { get; } = entityType;
}