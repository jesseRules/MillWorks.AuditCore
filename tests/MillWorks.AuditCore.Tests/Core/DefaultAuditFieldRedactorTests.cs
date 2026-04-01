using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class DefaultAuditFieldRedactorTests
{
    private DefaultAuditFieldRedactor _redactor = null!;

    [SetUp]
    public void SetUp()
    {
        _redactor = new DefaultAuditFieldRedactor();
    }

    [Test]
    public void RedactFields_RedactsNonSafeFields()
    {
        var fields = new Dictionary<string, object?>
        {
            ["UserId"] = Guid.NewGuid(),
            ["UserFullName"] = "John Doe",
            ["IpAddress"] = "192.168.1.1",
            ["EventType"] = "Login",
            ["Status"] = "Success",
            ["SensitiveData"] = "SSN-123-45-6789"
        };

        var result = _redactor.RedactFields(fields);

        // Safe fields pass through
        Assert.That(result["EventType"], Is.EqualTo("Login"));
        Assert.That(result["Status"], Is.EqualTo("Success"));

        // Non-safe fields are redacted
        Assert.That(result["UserId"], Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask));
        Assert.That(result["UserFullName"], Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask));
        Assert.That(result["IpAddress"], Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask));
        Assert.That(result["SensitiveData"], Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask));
    }

    [Test]
    public void RedactFields_PreservesAllSafeFields()
    {
        var safeFields = new Dictionary<string, object?>
        {
            ["EventType"] = "Audit.Created",
            ["OperationType"] = "UserLogin",
            ["Status"] = "Active",
            ["Action"] = "Created",
            ["Environment"] = "Production",
            ["MachineName"] = "Server01",
            ["AssemblyName"] = "MyApp",
            ["CallingMethodName"] = "DoWork",
            ["CorrelationId"] = "abc-123",
            ["Duration"] = 150,
            ["Success"] = true,
            ["ErrorMessage"] = "None",
            ["RequestMethod"] = "POST"
        };

        var result = _redactor.RedactFields(safeFields);

        foreach (var kvp in safeFields)
        {
            Assert.That(result[kvp.Key], Is.EqualTo(kvp.Value),
                $"Safe field '{kvp.Key}' should pass through unchanged");
        }
    }

    [Test]
    public void RedactFields_ReturnsNewDictionary()
    {
        var original = new Dictionary<string, object?> { ["key"] = "value" };
        var result = _redactor.RedactFields(original);

        Assert.That(result, Is.Not.SameAs(original));
    }

    [Test]
    public void RedactFields_EmptyDictionary_ReturnsEmpty()
    {
        var result = _redactor.RedactFields(new Dictionary<string, object?>());
        Assert.That(result, Is.Empty);
    }

    [Test]
    public void RedactValue_SafeField_ReturnsValue()
    {
        var result = _redactor.RedactValue("Environment", "Production");
        Assert.That(result, Is.EqualTo("Production"));
    }

    [Test]
    public void RedactValue_NonSafeField_ReturnsMask()
    {
        var result = _redactor.RedactValue("UserFullName", "John Doe");
        Assert.That(result, Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask));
    }

    [Test]
    public void RedactValue_NullValue_ReturnsNull()
    {
        var result = _redactor.RedactValue("UserFullName", null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void RedactValue_CaseInsensitive()
    {
        Assert.That(_redactor.RedactValue("eventtype", "Login"), Is.EqualTo("Login"));
        Assert.That(_redactor.RedactValue("EVENTTYPE", "Login"), Is.EqualTo("Login"));
        Assert.That(_redactor.RedactValue("EventType", "Login"), Is.EqualTo("Login"));
    }

    [Test]
    public void RedactTarget_NullTarget_ReturnsNull()
    {
        var result = _redactor.RedactTarget(null);
        Assert.That(result, Is.Null);
    }

    [Test]
    public void RedactTarget_PreservesType_RedactsSnapshots()
    {
        var target = new AuditTarget
        {
            Type = "UserEntity",
            Old = new { Name = "John", SSN = "123-45-6789" },
            New = new { Name = "Jane", SSN = "987-65-4321" }
        };

        var result = _redactor.RedactTarget(target);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo("UserEntity"));
        // Old and New should be replaced with redacted placeholders
        Assert.That(result.Old, Is.Not.Null);
        Assert.That(result.New, Is.Not.Null);
        Assert.That(result.Old!.ToString(), Does.Not.Contain("John"));
        Assert.That(result.New!.ToString(), Does.Not.Contain("Jane"));
    }

    [Test]
    public void RedactTarget_NullOld_ReturnsNullOld()
    {
        var target = new AuditTarget
        {
            Type = "Entity",
            Old = null,
            New = new { Name = "test" }
        };

        var result = _redactor.RedactTarget(target);
        Assert.That(result!.Old, Is.Null);
        Assert.That(result.New, Is.Not.Null);
    }

    [Test]
    public void RedactFields_FerpaMetadataFields_PassThrough()
    {
        var fields = new Dictionary<string, object?>
        {
            ["_FerpaEventType"] = "FERPA.StudentRecord.Updated",
            ["_ConsentRequired"] = true,
            ["_RecordType"] = "EducationRecord",
            ["_serializationError"] = true,
            ["_entityName"] = "StudentEntity",
            ["_action"] = "Modified"
        };

        var result = _redactor.RedactFields(fields);

        foreach (var kvp in fields)
        {
            Assert.That(result[kvp.Key], Is.EqualTo(kvp.Value),
                $"FERPA metadata field '{kvp.Key}' should pass through unchanged");
        }
    }

    [Test]
    public void RedactFields_PropertyNamesField_IsRedacted()
    {
        var fields = new Dictionary<string, object?>
        {
            ["_propertyNames"] = new List<string> { "Diagnosis", "SSN" }
        };

        var result = _redactor.RedactFields(fields);

        Assert.That(result["_propertyNames"], Is.EqualTo(DefaultAuditFieldRedactor.RedactionMask),
            "_propertyNames should be redacted because property names can reveal sensitive metadata");
    }
}
