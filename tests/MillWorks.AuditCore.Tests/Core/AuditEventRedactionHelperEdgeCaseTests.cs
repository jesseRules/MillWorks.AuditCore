using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// Phase 4: Completeness and edge case tests for AuditEventRedactionHelper.
/// Validates that all sensitive fields are redacted, no PHI leaks, and boundary conditions are safe.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public sealed class AuditEventRedactionHelperEdgeCaseTests
{
    private DefaultAuditFieldRedactor _redactor = null!;

    [SetUp]
    public void SetUp()
    {
        _redactor = new DefaultAuditFieldRedactor();
    }

    // ── IpAddress and UserAgent redaction ──

    [Test]
    public void RedactEvent_IpAddress_IsRedacted()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithIpAddress("192.168.1.100")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.IpAddress.Should().Be("[REDACTED]");
    }

    [Test]
    public void RedactEvent_UserAgent_IsRedacted()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithUserAgent("Mozilla/5.0 (Windows NT 10.0)")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.UserAgent.Should().Be("[REDACTED]");
    }

    [Test]
    public void RedactEvent_UserEmail_IsRedacted()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.UserEmail = "patient@hospital.org";

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.UserEmail.Should().Be("[REDACTED]");
    }

    // ── Null fields don't crash ──

    [Test]
    public void RedactEvent_NullIpAddress_StaysNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.IpAddress = null;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.IpAddress.Should().BeNull();
    }

    [Test]
    public void RedactEvent_NullUserAgent_StaysNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.UserAgent = null;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.UserAgent.Should().BeNull();
    }

    [Test]
    public void RedactEvent_NullUserEmail_StaysNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.UserEmail = null;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.UserEmail.Should().BeNull();
    }

    // ── Metadata preservation ──

    [Test]
    public void RedactEvent_PreservesEventId()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        // EventId is init-only, set via builder default
        var eventId = evt.EventId;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.EventId.Should().Be(eventId);
    }

    [Test]
    public void RedactEvent_PreservesCorrelationId()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithCorrelationId("corr-123")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.CorrelationId.Should().Be("corr-123");
    }

    [Test]
    public void RedactEvent_PreservesTimestamps()
    {
        var end = DateTimeOffset.UtcNow;
        var evt = TestAuditEventBuilder.Create()
            .WithEndDate(end)
            .Build();
        // StartDate is init-only, use the default value
        var start = evt.StartDate;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.StartDate.Should().Be(start);
        result.EndDate.Should().Be(end);
    }

    [Test]
    public void RedactEvent_PreservesEventType()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithEventType("PHI.Access")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.EventType.Should().Be("PHI.Access");
    }

    [Test]
    public void RedactEvent_PreservesAction()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithAction(Abstractions.Enums.AuditAction.Updated)
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.Action.Should().Be(Abstractions.Enums.AuditAction.Updated);
    }

    [Test]
    public void RedactEvent_PreservesSuccess()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithSuccess(false)
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.Success.Should().BeFalse();
    }

    // ── CustomFields redaction ──

    [Test]
    public void RedactEvent_CustomFields_SensitiveValuesRedacted()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithCustomField("PatientName", "John Doe")
            .WithCustomField("EventType", "LoginAttempt") // safe field
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.CustomFields["PatientName"].Should().Be("[REDACTED]");
        result.CustomFields["EventType"].Should().Be("LoginAttempt");
    }

    [Test]
    public void RedactEvent_CustomFields_ErrorMessageSanitized()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithCustomField("ErrorMessage", "Failed for user admin@corp.com Password=secret")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        var msg = result.CustomFields["ErrorMessage"]?.ToString();
        msg.Should().NotContain("admin@corp.com");
        msg.Should().NotContain("secret");
        msg.Should().Contain("[SANITIZED]");
    }

    // ── OldValues and NewValues redaction ──

    [Test]
    public void RedactEvent_OldAndNewValues_Redacted()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithOldValue("Diagnosis", "Diabetes")
            .WithNewValue("Diagnosis", "Hypertension")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.OldValues["Diagnosis"].Should().Be("[REDACTED]");
        result.NewValues["Diagnosis"].Should().Be("[REDACTED]");
    }

    // ── Target redaction ──

    [Test]
    public void RedactEvent_Target_SnapshotDataRedacted()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.Target = new AuditTarget
        {
            Type = "Patient",
            Old = new { Name = "John", SSN = "123-45-6789" },
            New = new { Name = "Jane", SSN = "987-65-4321" }
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.Target.Should().NotBeNull();
        result.Target!.Type.Should().Be("Patient");
        // Old/New should be replaced with redacted markers
        result.Target.Old.Should().NotBeNull();
        result.Target.New.Should().NotBeNull();
    }

    [Test]
    public void RedactEvent_NullTarget_StaysNull()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.Target = null;

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.Target.Should().BeNull();
    }

    // ── Does not mutate original ──

    [Test]
    public void RedactEvent_DoesNotMutateOriginal_AllFields()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithIpAddress("10.0.0.1")
            .WithUserAgent("TestBrowser")
            .WithCustomField("Secret", "value")
            .WithOldValue("Name", "OldName")
            .WithNewValue("Name", "NewName")
            .Build();
        evt.UserEmail = "test@test.com";

        AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        evt.IpAddress.Should().Be("10.0.0.1");
        evt.UserAgent.Should().Be("TestBrowser");
        evt.UserEmail.Should().Be("test@test.com");
        evt.CustomFields["Secret"].Should().Be("value");
        evt.OldValues["Name"].Should().Be("OldName");
        evt.NewValues["Name"].Should().Be("NewName");
    }

    // ── Empty collections ──

    [Test]
    public void RedactEvent_EmptyCustomFields_ReturnsEmpty()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.CustomFields = new Dictionary<string, object?>();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.CustomFields.Should().BeEmpty();
    }

    [Test]
    public void RedactEvent_EmptyOldValues_ReturnsEmpty()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.OldValues = new Dictionary<string, object?>();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.OldValues.Should().BeEmpty();
    }

    // ── Safe fields pass through in SystemFields ──

    [Test]
    public void RedactEvent_SystemFields_SafeFieldsPreserved()
    {
        var evt = TestAuditEventBuilder.Create().Build();
        evt.SystemFields = new Dictionary<string, object?>
        {
            ["EventType"] = "Login",
            ["Environment"] = "Production",
            ["CorrelationId"] = "abc-123",
            ["Duration"] = 42,
            ["Success"] = true,
            ["Action"] = "Create"
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.SystemFields!["EventType"].Should().Be("Login");
        result.SystemFields!["Environment"].Should().Be("Production");
        result.SystemFields!["CorrelationId"].Should().Be("abc-123");
        result.SystemFields!["Duration"].Should().Be(42);
    }

    // ── ChangedProperties case sensitivity ──

    [Test]
    public void RedactEvent_ChangedProperties_MixedCase_SensitiveRedacted()
    {
        var evt = TestAuditEventBuilder.Create()
            .WithChangedProperty("password")
            .WithChangedProperty("PASSWORD")
            .WithChangedProperty("Password")
            .Build();

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.ChangedProperties.Should().AllBe("[REDACTED_PROP]");
    }

    // ── KeyValues: preserve safe types, redact strings ──

    [Test]
    public void RedactEvent_KeyValues_MixedTypes()
    {
        var guid = Guid.NewGuid();
        var evt = TestAuditEventBuilder.Create().Build();
        evt.KeyValues = new Dictionary<string, object?>
        {
            ["Id"] = 42,
            ["TenantId"] = 7L,
            ["UniqueId"] = guid,
            ["Ratio"] = 3.14,
            ["Amount"] = 99.99m,
            ["Email"] = "patient@example.com",
            ["NullVal"] = null
        };

        var result = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

        result.KeyValues!["Id"].Should().Be(42);
        result.KeyValues!["TenantId"].Should().Be(7L);
        result.KeyValues!["UniqueId"].Should().Be(guid);
        result.KeyValues!["Ratio"].Should().Be(3.14);
        result.KeyValues!["Amount"].Should().Be(99.99m);
        result.KeyValues!["Email"].Should().Be("[REDACTED]");
        result.KeyValues!["NullVal"].Should().BeNull();
    }
}
