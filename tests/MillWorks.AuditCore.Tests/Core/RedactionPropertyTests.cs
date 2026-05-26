using System.Text.Json;
using FluentAssertions;
using MillWorks.AuditCore.Services;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// Phase 5: Property-based tests for the redaction pipeline.
/// Verifies non-leakage, idempotence, preservation, null safety, and structural integrity.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class RedactionPropertyTests
{
    private DefaultAuditFieldRedactor _redactor = null!;
    private static readonly Random Rng = new(42);

    [SetUp]
    public void SetUp()
    {
        _redactor = new DefaultAuditFieldRedactor();
    }

    // ── Non-leakage: sensitive field values never appear in redacted output ──

    [Test]
    public void Property_NonLeakage_SensitiveValuesNeverInOutput()
    {
        for (var i = 0; i < 500; i++)
        {
            var sensitiveIp = $"10.{Rng.Next(256)}.{Rng.Next(256)}.{Rng.Next(256)}";
            var sensitiveAgent = $"CustomBrowser/{Rng.Next(1000)}";
            var sensitiveEmail = $"patient{Rng.Next(10000)}@hospital.org";

            var evt = TestAuditEventBuilder.Create()
                .WithIpAddress(sensitiveIp)
                .WithUserAgent(sensitiveAgent)
                .Build();
            evt.UserEmail = sensitiveEmail;

            var redacted = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            // Serialize the entire redacted event and search for leaked values
            var serialized = JsonSerializer.Serialize(redacted);
            serialized.Should().NotContain(sensitiveIp,
                $"iteration {i}: IpAddress must not leak");
            serialized.Should().NotContain(sensitiveAgent,
                $"iteration {i}: UserAgent must not leak");
            serialized.Should().NotContain(sensitiveEmail,
                $"iteration {i}: UserEmail must not leak");
        }
    }

    // ── Idempotence: double redaction = single redaction ──

    [Test]
    public void Property_Idempotence_DoubleRedactionEqualsOne()
    {
        for (var i = 0; i < 500; i++)
        {
            var evt = TestAuditEventBuilder.Create()
                .WithIpAddress($"192.168.{Rng.Next(256)}.{Rng.Next(256)}")
                .WithUserAgent($"Agent/{Rng.Next(100)}")
                .WithCustomField("Secret", $"value_{Rng.Next(1000)}")
                .WithCustomField("EventType", "Safe") // safe field
                .Build();
            evt.UserEmail = $"user{Rng.Next(1000)}@example.com";

            var once = AuditEventRedactionHelper.RedactEvent(_redactor, evt);
            var twice = AuditEventRedactionHelper.RedactEvent(_redactor, once);

            var serializedOnce = JsonSerializer.Serialize(once);
            var serializedTwice = JsonSerializer.Serialize(twice);

            serializedOnce.Should().Be(serializedTwice,
                $"iteration {i}: redacting twice must produce same result as once");
        }
    }

    // ── Preservation: non-sensitive metadata fields preserved exactly ──

    [Test]
    public void Property_Preservation_MetadataFieldsPreserved()
    {
        for (var i = 0; i < 500; i++)
        {
            var eventType = $"Test.Event.{Rng.Next(100)}";

            var evt = TestAuditEventBuilder.Create()
                .WithEventType(eventType)
                .WithCorrelationId("user@example.com-" + Guid.NewGuid())
                .WithIpAddress("10.0.0.1") // sensitive - will be redacted
                .Build();

            var redacted = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            redacted.EventType.Should().Be(eventType,
                $"iteration {i}: EventType must be preserved");
            redacted.CorrelationId.Should().Be("[REDACTED]",
                $"iteration {i}: CorrelationId is redacted by default (safe-by-default posture)");
            redacted.EventId.Should().Be(evt.EventId);
            redacted.Action.Should().Be(evt.Action);
            redacted.EntityName.Should().Be(evt.EntityName);
            redacted.Success.Should().Be(evt.Success);
        }
    }

    // ── Null preservation: null sensitive fields stay null ──

    [Test]
    public void Property_NullPreservation_NullFieldsStayNull()
    {
        for (var i = 0; i < 500; i++)
        {
            var evt = TestAuditEventBuilder.Create().Build();
            // Randomly set some nullable fields to null
            evt.IpAddress = Rng.Next(2) == 0 ? null : "10.0.0.1";
            evt.UserAgent = Rng.Next(2) == 0 ? null : "Agent";
            evt.UserEmail = Rng.Next(2) == 0 ? null : "test@test.com";

            var redacted = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            if (evt.IpAddress is null)
                redacted.IpAddress.Should().BeNull($"iteration {i}: null IpAddress must stay null");
            if (evt.UserAgent is null)
                redacted.UserAgent.Should().BeNull($"iteration {i}: null UserAgent must stay null");
            if (evt.UserEmail is null)
                redacted.UserEmail.Should().BeNull($"iteration {i}: null UserEmail must stay null");
        }
    }

    // ── Structural integrity: redacted event can serialize/deserialize ──

    [Test]
    public void Property_StructuralIntegrity_RedactedEventSerializesCleanly()
    {
        for (var i = 0; i < 500; i++)
        {
            var evt = TestAuditEventBuilder.Create()
                .WithCustomField("key" + Rng.Next(100), "val" + Rng.Next(100))
                .WithOldValue("prop" + Rng.Next(10), "old" + Rng.Next(10))
                .WithNewValue("prop" + Rng.Next(10), "new" + Rng.Next(10))
                .WithIpAddress("10.0.0.1")
                .Build();

            var redacted = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            var act = () => JsonSerializer.Serialize(redacted);
            act.Should().NotThrow($"iteration {i}: redacted event must serialize without error");
        }
    }

    // ── CustomFields: sensitive values always redacted ──

    [Test]
    public void Property_CustomFields_SensitiveValuesAlwaysRedacted()
    {
        var sensitiveKeys = new[] { "PatientName", "SSN", "Diagnosis", "CustomSecret" };
        var safeKeys = new[] { "EventType", "Environment", "Action", "Status" };

        for (var i = 0; i < 300; i++)
        {
            var builder = TestAuditEventBuilder.Create();

            var sensitiveKey = sensitiveKeys[Rng.Next(sensitiveKeys.Length)];
            var sensitiveValue = $"sensitive_data_{Rng.Next(10000)}";
            builder.WithCustomField(sensitiveKey, sensitiveValue);

            var safeKey = safeKeys[Rng.Next(safeKeys.Length)];
            var safeValue = $"safe_data_{Rng.Next(10000)}";
            builder.WithCustomField(safeKey, safeValue);

            var evt = builder.Build();
            var redacted = AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            redacted.CustomFields[sensitiveKey].Should().Be("[REDACTED]",
                $"iteration {i}: '{sensitiveKey}' must be redacted");
            redacted.CustomFields[safeKey].Should().Be(safeValue,
                $"iteration {i}: '{safeKey}' must be preserved");
        }
    }

    // ── Original event immutability ──

    [Test]
    public void Property_Immutability_OriginalEventNeverMutated()
    {
        for (var i = 0; i < 300; i++)
        {
            var ip = $"10.{Rng.Next(256)}.{Rng.Next(256)}.{Rng.Next(256)}";
            var agent = $"Agent-{Rng.Next(1000)}";
            var customVal = $"secret-{Rng.Next(1000)}";

            var evt = TestAuditEventBuilder.Create()
                .WithIpAddress(ip)
                .WithUserAgent(agent)
                .WithCustomField("Data", customVal)
                .Build();

            AuditEventRedactionHelper.RedactEvent(_redactor, evt);

            evt.IpAddress.Should().Be(ip);
            evt.UserAgent.Should().Be(agent);
            evt.CustomFields["Data"].Should().Be(customVal);
        }
    }
}
