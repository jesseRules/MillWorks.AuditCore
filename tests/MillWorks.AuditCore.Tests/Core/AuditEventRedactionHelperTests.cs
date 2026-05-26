using FluentAssertions;
using MillWorks.AuditCore.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class AuditEventRedactionHelperTests
{
    private DefaultAuditFieldRedactor _redactor = null!;

    [SetUp]
    public void SetUp()
    {
        _redactor = new DefaultAuditFieldRedactor();
    }

    // --- ChangedProperties (Fix 2) ---

    [Test]
    public void RedactEvent_ChangedProperties_RedactsSensitiveNames()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithChangedProperty("SSN")
            .WithChangedProperty("Status")
            .WithChangedProperty("Diagnosis")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ChangedProperties.Should().Contain("Status");
        result.ChangedProperties.Should().NotContain("SSN");
        result.ChangedProperties.Should().NotContain("Diagnosis");
        result.ChangedProperties.Where(p => p == "[REDACTED_PROP]").Should().HaveCount(2);
    }

    [Test]
    public void RedactEvent_ChangedProperties_PreservesNonSensitiveNames()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithChangedProperty("IsActive")
            .WithChangedProperty("UpdatedAt")
            .WithChangedProperty("Name")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ChangedProperties.Should().BeEquivalentTo(["IsActive", "UpdatedAt", "Name"]);
    }

    [Test]
    public void RedactEvent_ChangedProperties_EmptyList_ReturnsEmpty()
    {
        var evt = TestAuditEventBuilder.Create().Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ChangedProperties.Should().BeEmpty();
    }

    [Test]
    public void RedactEvent_ChangedProperties_CaseInsensitive()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithChangedProperty("ssn")
            .WithChangedProperty("PASSWORD")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ChangedProperties.Should().AllBe("[REDACTED_PROP]");
    }

    [Test]
    public void RedactEvent_DoesNotMutateOriginal_ChangedProperties()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithChangedProperty("SSN")
            .Build();

        AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        evt.ChangedProperties.Should().Contain("SSN");
    }

    // --- KeyValues (Fix 3) ---

    [Test]
    public void RedactEvent_KeyValues_PreservesNumericKeys()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = new Dictionary<string, object?> { ["Id"] = 42, ["TenantId"] = 7L };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.KeyValues!["Id"].Should().Be(42);
        result.KeyValues!["TenantId"].Should().Be(7L);
    }

    [Test]
    public void RedactEvent_KeyValues_PreservesGuidKeys()
    {
        var guid = Guid.NewGuid();
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = new Dictionary<string, object?> { ["Id"] = guid };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.KeyValues!["Id"].Should().Be(guid);
    }

    [Test]
    public void RedactEvent_KeyValues_RedactsStringValues()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = new Dictionary<string, object?>
        {
            ["Email"] = "patient@hospital.org",
            ["SSN"] = "123-45-6789"
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.KeyValues!["Email"].Should().Be("[REDACTED]");
        result.KeyValues!["SSN"].Should().Be("[REDACTED]");
    }

    [Test]
    public void RedactEvent_KeyValues_Null_ReturnsNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = null!;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.KeyValues.Should().BeNull();
    }

    [Test]
    public void RedactEvent_KeyValues_DoesNotMutateOriginal()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = new Dictionary<string, object?> { ["Email"] = "test@test.com" };

        AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        evt.KeyValues["Email"].Should().Be("test@test.com");
    }

    // --- SystemFields (Fix 4) ---

    [Test]
    public void RedactEvent_SystemFields_RedactsUnknownFields()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = new Dictionary<string, object?>
        {
            ["PatientId"] = "P-12345",
            ["InternalNotes"] = "Sensitive clinical note"
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SystemFields!["PatientId"].Should().Be("[REDACTED]");
        result.SystemFields!["InternalNotes"].Should().Be("[REDACTED]");
    }

    [Test]
    public void RedactEvent_SystemFields_PreservesSafeFields()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = new Dictionary<string, object?>
        {
            ["EventType"] = "UserLogin",
            ["Environment"] = "Production"
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SystemFields!["EventType"].Should().Be("UserLogin");
        result.SystemFields!["Environment"].Should().Be("Production");
    }

    [Test]
    public void RedactEvent_SystemFields_Null_ReturnsNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = null!;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SystemFields.Should().BeNull();
    }

    [Test]
    public void RedactEvent_SystemFields_DoesNotMutateOriginal()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = new Dictionary<string, object?> { ["Secret"] = "value" };

        AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        evt.SystemFields["Secret"].Should().Be("value");
    }

    // --- ErrorMessage redaction (Finding #1 - DLQ redaction incomplete) ---

    [Test]
    public void RedactEvent_ErrorMessage_RedactsSensitiveContent()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithError("Connection string: Server=prod;Password=secret123")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ErrorMessage.Should().NotContain("secret123");
        result.ErrorMessage.Should().NotContain("Password=");
    }

    [Test]
    public void RedactEvent_ErrorMessage_RedactsPasswordPatterns()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithError("Failed to connect: Server=db.prod.local;User Id=admin;Password=s3cr3tP@ss;")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ErrorMessage.Should().NotContain("s3cr3tP@ss");
        result.ErrorMessage.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void RedactEvent_ErrorMessage_NullStaysNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.ErrorMessage = null;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ErrorMessage.Should().BeNull();
    }

    [Test]
    public void RedactEvent_ErrorMessage_DoesNotMutateOriginal()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithError("Password=secret")
            .Build();

        AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        evt.ErrorMessage.Should().Contain("Password=secret");
    }

    // --- CorrelationId redaction (Finding #4 - CorrelationId may contain PII) ---

    [Test]
    public void RedactEvent_CorrelationId_IsRedactedByDefault()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithCorrelationId("user@example.com-12345")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.CorrelationId.Should().Be("[REDACTED]");
    }

    [Test]
    public void RedactEvent_SystemFields_CorrelationIdFieldIsRedacted()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = new Dictionary<string, object?>
        {
            ["CorrelationId"] = "user-123@tenant.com"
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SystemFields!["CorrelationId"].Should().Be("[REDACTED]");
    }

    // --- SessionId redaction (same rationale as CorrelationId) ---

    [Test]
    public void RedactEvent_SessionId_IsRedactedByDefault()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SessionId = "user-session-abc123";

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SessionId.Should().Be("[REDACTED]");
    }
}
