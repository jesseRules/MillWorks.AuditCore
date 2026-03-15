using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public class AuditEventModelTests
{
    [Test]
    public void NewEvent_HasUniqueEventId()
    {
        var event1 = new AuditEvent();
        var event2 = new AuditEvent();

        Assert.That(event1.EventId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(event2.EventId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(event1.EventId, Is.Not.EqualTo(event2.EventId));
    }

    [Test]
    public void NewEvent_StartDateIsPopulated()
    {
        var before = DateTimeOffset.UtcNow;
        var auditEvent = new AuditEvent();
        var after = DateTimeOffset.UtcNow;

        Assert.That(auditEvent.StartDate, Is.GreaterThanOrEqualTo(before));
        Assert.That(auditEvent.StartDate, Is.LessThanOrEqualTo(after));
    }

    [Test]
    public void NewEvent_DefaultValues()
    {
        var auditEvent = new AuditEvent();

        Assert.That(auditEvent.EventType, Is.EqualTo(string.Empty));
        Assert.That(auditEvent.EndDate, Is.Null);
        Assert.That(auditEvent.Duration, Is.Null);
        Assert.That(auditEvent.Target, Is.Null);
        Assert.That(auditEvent.Success, Is.Null);
        Assert.That(auditEvent.ErrorMessage, Is.Null);
        Assert.That(auditEvent.CorrelationId, Is.Null);
        Assert.That(auditEvent.EntityName, Is.EqualTo(string.Empty));
        Assert.That(auditEvent.Action, Is.EqualTo(MillWorks.AuditCore.Abstractions.Enums.AuditAction.Unknown));
    }

    [Test]
    public void NewEvent_CollectionsInitializedEmpty()
    {
        var auditEvent = new AuditEvent();

        Assert.That(auditEvent.CustomFields, Is.Not.Null);
        Assert.That(auditEvent.CustomFields, Is.Empty);
        Assert.That(auditEvent.SystemFields, Is.Not.Null);
        Assert.That(auditEvent.SystemFields, Is.Empty);
        Assert.That(auditEvent.KeyValues, Is.Not.Null);
        Assert.That(auditEvent.KeyValues, Is.Empty);
        Assert.That(auditEvent.OldValues, Is.Not.Null);
        Assert.That(auditEvent.OldValues, Is.Empty);
        Assert.That(auditEvent.NewValues, Is.Not.Null);
        Assert.That(auditEvent.NewValues, Is.Empty);
        Assert.That(auditEvent.ChangedProperties, Is.Not.Null);
        Assert.That(auditEvent.ChangedProperties, Is.Empty);
    }

    [Test]
    public void CustomFields_AddAndRetrieve()
    {
        var auditEvent = new AuditEvent();

        auditEvent.CustomFields["UserId"] = "user-123";
        auditEvent.CustomFields["Action"] = "Login";
        auditEvent.CustomFields["Count"] = 42;

        Assert.That(auditEvent.CustomFields, Has.Count.EqualTo(3));
        Assert.That(auditEvent.CustomFields["UserId"], Is.EqualTo("user-123"));
        Assert.That(auditEvent.CustomFields["Count"], Is.EqualTo(42));
    }

    [Test]
    public void SystemFields_AddAndRetrieve()
    {
        var auditEvent = new AuditEvent();

        auditEvent.SystemFields["DatabaseId"] = 999L;
        auditEvent.SystemFields["Version"] = "1.0";

        Assert.That(auditEvent.SystemFields, Has.Count.EqualTo(2));
        Assert.That(auditEvent.SystemFields["DatabaseId"], Is.EqualTo(999L));
    }

    [Test]
    public void CalculateDuration_WithEndDate_SetsDuration()
    {
        var auditEvent = new AuditEvent();
        auditEvent.EndDate = auditEvent.StartDate.AddMilliseconds(1500);

        auditEvent.CalculateDuration();

        Assert.That(auditEvent.Duration, Is.EqualTo(1500));
    }

    [Test]
    public void CalculateDuration_WithoutEndDate_DurationRemainsNull()
    {
        var auditEvent = new AuditEvent();

        auditEvent.CalculateDuration();

        Assert.That(auditEvent.Duration, Is.Null);
    }

    [Test]
    public void ChangedProperties_AddAndEnumerate()
    {
        var auditEvent = new AuditEvent();

        auditEvent.ChangedProperties.Add("Name");
        auditEvent.ChangedProperties.Add("Email");

        Assert.That(auditEvent.ChangedProperties, Has.Count.EqualTo(2));
        Assert.That(auditEvent.ChangedProperties, Does.Contain("Name"));
        Assert.That(auditEvent.ChangedProperties, Does.Contain("Email"));
    }

    [Test]
    public void OldValues_NewValues_TrackChanges()
    {
        var auditEvent = new AuditEvent();

        auditEvent.OldValues["Name"] = "John";
        auditEvent.NewValues["Name"] = "Jane";
        auditEvent.KeyValues["Id"] = Guid.NewGuid();

        Assert.That(auditEvent.OldValues["Name"], Is.EqualTo("John"));
        Assert.That(auditEvent.NewValues["Name"], Is.EqualTo("Jane"));
        Assert.That(auditEvent.KeyValues, Has.Count.EqualTo(1));
    }

    [Test]
    public void Environment_DefaultsToNewInstance()
    {
        var auditEvent = new AuditEvent();

        Assert.That(auditEvent.Environment, Is.Not.Null);
        Assert.That(auditEvent.Environment, Is.InstanceOf<AuditEnvironment>());
    }

    [Test]
    public void Target_CanBeSetWithValues()
    {
        var auditEvent = new AuditEvent
        {
            Target = new AuditTarget
            {
                Type = "User",
                Old = new { Name = "Old" },
                New = new { Name = "New" }
            }
        };

        Assert.That(auditEvent.Target, Is.Not.Null);
        Assert.That(auditEvent.Target.Type, Is.EqualTo("User"));
    }

    [Test]
    public void EventId_IsInitOnly()
    {
        var specificId = Guid.NewGuid();
        var auditEvent = new AuditEvent { EventId = specificId };

        Assert.That(auditEvent.EventId, Is.EqualTo(specificId));
    }
}

[TestFixture]
[Category("Unit")]
public class AuditEnvironmentTests
{
    [Test]
    public void NewEnvironment_AllPropertiesNull()
    {
        var env = new AuditEnvironment();

        Assert.That(env.UserName, Is.Null);
        Assert.That(env.MachineName, Is.Null);
        Assert.That(env.DomainName, Is.Null);
        Assert.That(env.CallingMethodName, Is.Null);
        Assert.That(env.AssemblyName, Is.Null);
        Assert.That(env.Culture, Is.Null);
    }

    [Test]
    public void Environment_PropertiesCanBeSet()
    {
        var env = new AuditEnvironment
        {
            UserName = "admin",
            MachineName = "SERVER-01",
            DomainName = "CORP",
            CallingMethodName = "ProcessOrder",
            AssemblyName = "MyApp",
            Culture = "en-US"
        };

        Assert.That(env.UserName, Is.EqualTo("admin"));
        Assert.That(env.MachineName, Is.EqualTo("SERVER-01"));
        Assert.That(env.Culture, Is.EqualTo("en-US"));
    }
}

[TestFixture]
[Category("Unit")]
public class AuditTargetTests
{
    [Test]
    public void NewTarget_AllPropertiesNull()
    {
        var target = new AuditTarget();

        Assert.That(target.Type, Is.Null);
        Assert.That(target.Old, Is.Null);
        Assert.That(target.New, Is.Null);
    }

    [Test]
    public void Target_CanHoldComplexObjects()
    {
        var oldState = new { Name = "Old", Value = 1 };
        var newState = new { Name = "New", Value = 2 };

        var target = new AuditTarget
        {
            Type = "Configuration",
            Old = oldState,
            New = newState
        };

        Assert.That(target.Type, Is.EqualTo("Configuration"));
        Assert.That(target.Old, Is.EqualTo(oldState));
        Assert.That(target.New, Is.EqualTo(newState));
    }
}
