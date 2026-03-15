namespace MillWorks.AuditCore.EntityFramework.Primitives;

/// <summary>
/// Entity base class
/// </summary>
public abstract class AuditEntity
{
    /// <summary>
    /// Identifier of the entity
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Entity constructor
    /// </summary>
    protected AuditEntity() => Id = Guid.NewGuid();
    /// <summary>
    /// Entity constructor with specified ID
    /// </summary>
    /// <param name="id"></param>
    protected AuditEntity(Guid id) => Id = id;

    /// <summary>
    /// Equals override
    /// </summary>
    /// <param name="obj"></param>
    /// <returns></returns>
    public override bool Equals(object? obj)
    {
        if (obj is not AuditEntity entity) return false;
        if (ReferenceEquals(this, entity)) return true;
        if (entity.GetType() != GetType()) return false;
        return Id.Equals(entity.Id);
    }

    /// <summary>
    /// GetHashCode override
    /// </summary>
    /// <returns></returns>
    public override int GetHashCode() => Id.GetHashCode() * 41;

    /// <summary>
    /// Operator == overload
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator ==(AuditEntity? left, AuditEntity? right)
    {
        if (left is null) return right is null;
        return left.Equals(right);
    }

    /// <summary>
    /// Operator != overload
    /// </summary>
    /// <param name="left"></param>
    /// <param name="right"></param>
    /// <returns></returns>
    public static bool operator !=(AuditEntity? left, AuditEntity? right) => !(left == right);
}