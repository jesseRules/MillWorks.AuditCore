using MillWorks.AuditCore.EntityFramework.Common;

namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Aggregate root base class with audit, soft delete, and domain event support
/// </summary>
public abstract class AuditAggregateRoot : AuditEntity, IAuditableEntity, ISoftDeletable
{
    /// <summary>
    /// Domain events associated with this aggregate
    /// </summary>
    private readonly List<IAuditDomainEvent> _domainEvents = new();

    // Domain Events
    /// <summary>
    /// Domain events collection
    /// </summary>
    public IReadOnlyCollection<IAuditDomainEvent> DomainEvents => _domainEvents.AsReadOnly();

    // Audit Properties
    /// <summary>
    /// Creation timestamp
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Creator user ID
    /// </summary>
    public Guid CreatedById { get; set; }

    /// <summary>
    /// Update timestamp
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Updater user ID
    /// </summary>
    public Guid? UpdatedById { get; set; }

    // Soft Delete
    /// <summary>
    /// Indicates if the entity is soft deleted
    /// </summary>
    public bool IsDeleted { get; set; }

    /// <summary>
    /// Deletion timestamp
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Deleter user ID
    /// </summary>
    public Guid? DeletedById { get; set; }

    // Concurrency
    /// <summary>
    /// Row version for concurrency control
    /// </summary>
    public byte[] RowVersion { get; protected set; } = [];

    /// <summary>
    /// Aggregate root constructor
    /// </summary>
    protected AuditAggregateRoot() : base()
    {
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Aggregate root constructor with ID
    /// </summary>
    /// <param name="id"></param>
    protected AuditAggregateRoot(Guid id) : base(id)
    {
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Add a domain event to the aggregate
    /// </summary>
    /// <param name="domainEvent"></param>
    private void AddDomainEvent(IAuditDomainEvent domainEvent)
    {
        _domainEvents.Add(domainEvent);
    }

    /// <summary>
    /// Remove a domain event from the aggregate
    /// </summary>
    /// <param name="domainEvent"></param>
    protected void RemoveDomainEvent(IAuditDomainEvent domainEvent)
    {
        _domainEvents.Remove(domainEvent);
    }

    /// <summary>
    /// Clear all domain events from the aggregate
    /// </summary>
    public void ClearDomainEvents()
    {
        _domainEvents.Clear();
    }

    /// <summary>
    /// Delete the entity (soft delete)
    /// </summary>
    /// <param name="deletedBy"></param>
    public virtual void Delete(Guid deletedBy)
    {
        IsDeleted = true;
        DeletedAt = DateTimeOffset.UtcNow;
        DeletedById = deletedBy;
        AddDomainEvent(new AuditEntityDeletedEvent(Id, GetType().Name));
    }

    /// <summary>
    /// Set the creator user ID
    /// </summary>
    /// <param name="userId"></param>
    public virtual void SetCreatedBy(Guid userId)
    {
        CreatedById = userId;
    }

    /// <summary>
    /// Set the updater user ID and update timestamp
    /// </summary>
    /// <param name="userId"></param>
    public virtual void SetUpdatedBy(Guid userId)
    {
        UpdatedById = userId;
        UpdatedAt = DateTimeOffset.UtcNow;
    }
}