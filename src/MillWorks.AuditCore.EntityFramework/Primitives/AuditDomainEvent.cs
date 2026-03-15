namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Domain event base class
/// </summary>
public abstract class AuditDomainEvent : IAuditDomainEvent
{
    /// <summary>
    /// Identifier of the domain event
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Occurrence timestamp of the domain event
    /// </summary>
    public DateTimeOffset OccurredOn { get; }

    /// <summary>
    /// Domain event constructor
    /// </summary>
    protected AuditDomainEvent()
    {
        Id = Guid.NewGuid();
        OccurredOn = DateTimeOffset.UtcNow;
    }
}