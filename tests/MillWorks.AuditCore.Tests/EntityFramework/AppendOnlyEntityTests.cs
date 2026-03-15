using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Primitives;

namespace MillWorks.AuditCore.Tests.EntityFramework;

[TestFixture]
[Category("Unit")]
public class AppendOnlyEntityTests
{
    [Test]
    public void Constructor_GeneratesNewId()
    {
        var entity = new TestAppendOnlyEntity();

        Assert.That(entity.Id, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void Constructor_SetsCreatedAtToUtcNow()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var entity = new TestAppendOnlyEntity();

        Assert.That(entity.CreatedAt, Is.GreaterThan(before));
        Assert.That(entity.CreatedAt, Is.LessThanOrEqualTo(DateTimeOffset.UtcNow));
    }

    [Test]
    public void Constructor_WithId_UsesProvidedId()
    {
        var id = Guid.NewGuid();
        var entity = new TestAppendOnlyEntityWithId(id);

        Assert.That(entity.Id, Is.EqualTo(id));
        Assert.That(entity.CreatedAt, Is.Not.EqualTo(default(DateTimeOffset)));
    }

    [Test]
    public void InheritsFromAuditEntity()
    {
        var entity = new TestAppendOnlyEntity();

        Assert.That(entity, Is.InstanceOf<AuditEntity>());
    }

    [Test]
    public void HasOnlyIdCreatedAtCreatedById()
    {
        // AppendOnlyEntity should have exactly these properties (plus inherited Id)
        var type = typeof(AppendOnlyEntity);
        var declaredProperties = type.GetProperties(
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.DeclaredOnly);

        var propertyNames = declaredProperties.Select(p => p.Name).ToList();

        Assert.That(propertyNames, Does.Contain("CreatedAt"));
        Assert.That(propertyNames, Does.Contain("CreatedById"));
        Assert.That(propertyNames, Has.Count.EqualTo(2));
    }

    [Test]
    public void DoesNotHaveSoftDeleteProperties()
    {
        var type = typeof(AppendOnlyEntity);

        Assert.That(type.GetProperty("IsDeleted"), Is.Null);
        Assert.That(type.GetProperty("DeletedAt"), Is.Null);
        Assert.That(type.GetProperty("DeletedById"), Is.Null);
    }

    [Test]
    public void DoesNotHaveUpdateProperties()
    {
        var type = typeof(AppendOnlyEntity);

        Assert.That(type.GetProperty("UpdatedAt"), Is.Null);
        Assert.That(type.GetProperty("UpdatedById"), Is.Null);
    }

    [Test]
    public void DoesNotHaveRowVersionOrDomainEvents()
    {
        var type = typeof(AppendOnlyEntity);

        Assert.That(type.GetProperty("RowVersion"), Is.Null);
        Assert.That(type.GetProperty("DomainEvents"), Is.Null);
    }

    [Test]
    public void AuditIntegrityEntity_InheritsFromAppendOnly()
    {
        var entity = new AuditIntegrityEntity();

        Assert.That(entity, Is.InstanceOf<AppendOnlyEntity>());
        Assert.That(entity, Is.Not.InstanceOf<AuditAggregateRoot>());
    }

    [Test]
    public void AuditLogEntity_InheritsFromAppendOnly()
    {
        var entity = new AuditLogEntity();

        Assert.That(entity, Is.InstanceOf<AppendOnlyEntity>());
        Assert.That(entity, Is.Not.InstanceOf<AuditAggregateRoot>());
    }

    private class TestAppendOnlyEntity : AppendOnlyEntity
    {
    }

    private class TestAppendOnlyEntityWithId : AppendOnlyEntity
    {
        public TestAppendOnlyEntityWithId(Guid id) : base(id) { }
    }
}
