namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Event that represents a domain event
/// </summary>
public interface IAuditDomainEvent
{
    /// <summary>
    /// Identifier of the domain event
    /// </summary>
    Guid Id { get; }

    /// <summary>
    /// Occurrence timestamp of the domain event
    /// </summary>
    DateTimeOffset OccurredOn { get; }
}