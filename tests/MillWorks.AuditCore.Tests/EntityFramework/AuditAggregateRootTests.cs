using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public class AuditAggregateRootTests
{
    private TestAggregateRoot _entity;

    [SetUp]
    public void Setup()
    {
        _entity = new TestAggregateRoot();
    }

    [Test]
    public void Constructor_GeneratesNewId()
    {
        Assert.That(_entity.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void Constructor_SetsCreatedAtToUtcNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var entity = new TestAggregateRoot();

        Assert.That(entity.CreatedAt, Is.GreaterThan(before));
        Assert.That(entity.CreatedAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    [Test]
    public void Constructor_WithId_UsesProvidedId()
    {
        var id = Guid.NewGuid();
        var entity = new TestAggregateRootWithId(id);

        Assert.That(entity.Id, Is.EqualTo(id));
    }

    [Test]
    public void Constructor_DefaultValues()
    {
        Assert.That(_entity.IsDeleted, Is.False);
        Assert.That(_entity.DeletedAt, Is.Null);
        Assert.That(_entity.DeletedById, Is.Null);
        Assert.That(_entity.UpdatedAt, Is.Null);
        Assert.That(_entity.UpdatedById, Is.Null);
        Assert.That(_entity.RowVersion, Is.Empty);
        Assert.That(_entity.DomainEvents, Is.Empty);
    }

    [Test]
    public void Delete_SetsIsDeletedAndTimestamp()
    {
        var deletedBy = Guid.NewGuid();

        _entity.Delete(deletedBy);

        Assert.That(_entity.IsDeleted, Is.True);
        Assert.That(_entity.DeletedAt, Is.Not.Null);
        Assert.That(_entity.DeletedAt!.Value, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    [Test]
    public void Delete_SetsDeletedById()
    {
        var deletedBy = Guid.NewGuid();

        _entity.Delete(deletedBy);

        Assert.That(_entity.DeletedById, Is.EqualTo(deletedBy));
    }

    [Test]
    public void Delete_AddsDomainEvent()
    {
        _entity.Delete(Guid.NewGuid());

        Assert.That(_entity.DomainEvents, Has.Count.EqualTo(1));
        Assert.That(_entity.DomainEvents.First(), Is.InstanceOf<AuditEntityDeletedEvent>());
    }

    [Test]
    public void Delete_DomainEvent_HasCorrectEntityInfo()
    {
        var entityId = _entity.Id;

        _entity.Delete(Guid.NewGuid());

        var domainEvent = (AuditEntityDeletedEvent)_entity.DomainEvents.First();
        Assert.That(domainEvent.EntityId, Is.EqualTo(entityId));
        Assert.That(domainEvent.EntityType, Is.EqualTo(nameof(TestAggregateRoot)));
    }

    [Test]
    public void Delete_CalledTwice_AddsTwoDomainEvents()
    {
        _entity.Delete(Guid.NewGuid());
        _entity.Delete(Guid.NewGuid());

        Assert.That(_entity.DomainEvents, Has.Count.EqualTo(2));
    }

    [Test]
    public void SetCreatedBy_SetsCreatedById()
    {
        var userId = Guid.NewGuid();

        _entity.SetCreatedBy(userId);

        Assert.That(_entity.CreatedById, Is.EqualTo(userId));
    }

    [Test]
    public void SetUpdatedBy_SetsUpdatedByIdAndTimestamp()
    {
        var userId = Guid.NewGuid();

        _entity.SetUpdatedBy(userId);

        Assert.That(_entity.UpdatedById, Is.EqualTo(userId));
        Assert.That(_entity.UpdatedAt, Is.Not.Null);
        Assert.That(_entity.UpdatedAt!.Value, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    [Test]
    public void ClearDomainEvents_RemovesAllEvents()
    {
        _entity.Delete(Guid.NewGuid());
        _entity.Delete(Guid.NewGuid());
        Assert.That(_entity.DomainEvents, Has.Count.EqualTo(2));

        _entity.ClearDomainEvents();

        Assert.That(_entity.DomainEvents, Is.Empty);
    }

    [Test]
    public void ClearDomainEvents_EmptyCollection_NoOp()
    {
        Assert.DoesNotThrow(() => _entity.ClearDomainEvents());
        Assert.That(_entity.DomainEvents, Is.Empty);
    }

    [Test]
    public void DomainEvents_IsReadOnlyCollection()
    {
        var events = _entity.DomainEvents;

        Assert.That(events, Is.InstanceOf<IReadOnlyCollection<IAuditDomainEvent>>());
    }

    // Concrete test class since AuditAggregateRoot is abstract
    private class TestAggregateRoot : AuditAggregateRoot
    {
    }

    private class TestAggregateRootWithId : AuditAggregateRoot
    {
        public TestAggregateRootWithId(Guid id) : base(id) { }
    }
}

[TestFixture]
[Category("Unit")]
public class AuditEntityTests
{
    [Test]
    public void TwoEntities_SameId_AreEqual()
    {
        var id = Guid.NewGuid();
        var entity1 = new TestEntity(id);
        var entity2 = new TestEntity(id);

        Assert.That(entity1, Is.EqualTo(entity2));
        Assert.That(entity1 == entity2, Is.True);
    }

    [Test]
    public void TwoEntities_DifferentId_AreNotEqual()
    {
        var entity1 = new TestEntity();
        var entity2 = new TestEntity();

        Assert.That(entity1, Is.Not.EqualTo(entity2));
        Assert.That(entity1 != entity2, Is.True);
    }

    [Test]
    public void Entity_ComparedToNull_IsNotEqual()
    {
        var entity = new TestEntity();

        Assert.That(entity.Equals(null), Is.False);
        Assert.That(entity == null, Is.False);
    }

    [Test]
    public void Entity_ComparedToSelf_IsEqual()
    {
        var entity = new TestEntity();

        Assert.That(entity.Equals(entity), Is.True);
    }

    [Test]
    public void GetHashCode_SameId_SameHash()
    {
        var id = Guid.NewGuid();
        var entity1 = new TestEntity(id);
        var entity2 = new TestEntity(id);

        Assert.That(entity1.GetHashCode(), Is.EqualTo(entity2.GetHashCode()));
    }

    [Test]
    public void NullEntity_EqualToNullEntity_IsTrue()
    {
        TestEntity? a = null;
        TestEntity? b = null;

        Assert.That(a == b, Is.True);
    }

    private class TestEntity : AuditEntity
    {
        public TestEntity() { }
        public TestEntity(Guid id) : base(id) { }
    }
}
