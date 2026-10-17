using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Sinks.Identity;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Tests.Identity;

[TestFixture]
public sealed class AuditEnvelopeContentTests
{
    private static AuditEnvelope Event() => new()
    {
        Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "Invoice", Action = AuditAction.Updated,
        ExplicitEventId = Guid.NewGuid(), OccurredAt = DateTimeOffset.Parse("2026-10-16T12:00:00Z"),
        EventType = "Invoice.Updated", UserId = "actor-1", AdditionalData = "{\"amount\":1,\"meta\":{\"a\":true,\"b\":null}}"
    };

    [Test]
    public void CorrelationAndLogicalIdsAreStoredSeparatelyFromContent()
    {
        var original = Event();
        var reEnveloped = original with { EnvelopeId = Guid.NewGuid() };
        var newLogicalEvent = original with { ExplicitEventId = Guid.NewGuid() };
        Assert.That(AuditEnvelopeContent.Fingerprint(reEnveloped), Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
        Assert.That(AuditEnvelopeContent.Fingerprint(newLogicalEvent), Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [Test]
    public void JsonObjectOrderWhitespaceEscapesAndExactDecimalRepresentationsNormalize()
    {
        var original = Event();
        var changed = original with { AdditionalData = " { \"meta\" : { \"b\": null, \"\\u0061\":true }, \"amount\": 100e-2 } " };
        Assert.That(AuditEnvelopeContent.Fingerprint(changed), Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [TestCase("{\"n\":9007199254740992}", "{\"n\":9007199254740993}")]
    [TestCase("{\"n\":1234567890123456789012345678901234567890}", "{\"n\":1234567890123456789012345678901234567891}")]
    [TestCase("{\"n\":1e500}", "{\"n\":2e500}")]
    [TestCase("{\"a\":[1,2]}", "{\"a\":[2,1]}")]
    [TestCase("{\"a\":null}", "{}")]
    [TestCase("{\"s\":\"2026-10-16T12:00:00Z\"}", "{\"s\":\"2026-10-16T07:00:00-05:00\"}")]
    [TestCase("opaque data", "opaque data ")]
    public void ChangedPayloadCannotBeRoundedOrNormalizedAway(string before, string after)
    {
        var original = Event() with { AdditionalData = before };
        Assert.That(AuditEnvelopeContent.Fingerprint(original with { AdditionalData = after }),
            Is.Not.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [TestCase("{\"n\":1}", "{\"n\":1.000}")]
    [TestCase("{\"n\":1000}", "{\"n\":1e3}")]
    [TestCase("{\"n\":-0}", "{\"n\":0.000}")]
    [TestCase("{\"n\":0.001}", "{\"n\":10e-4}")]
    public void MathematicallyEqualDecimalValuesCompareEqual(string before, string after)
    {
        var original = Event() with { AdditionalData = before };
        Assert.That(AuditEnvelopeContent.Fingerprint(original with { AdditionalData = after }),
            Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [Test]
    public void ActorTenantResourceAndExplicitOccurrenceTimeAreSemantic()
    {
        var tenant = Guid.NewGuid();
        var original = Event() with { GovernanceIdentity = new(tenant, "invoice", Guid.NewGuid()) };
        var fingerprint = AuditEnvelopeContent.Fingerprint(original);
        AuditEnvelope[] changes =
        [
            original with { UserId = "actor-2" },
            original with { GovernanceIdentity = new(Guid.NewGuid(), "invoice", original.GovernanceIdentity!.ResourceId) },
            original with { GovernanceIdentity = new(tenant, "invoice", Guid.NewGuid()) },
            original with { OccurredAt = original.OccurredAt.AddTicks(1) },
            original with { CorrelationId = "different-operation" },
            original with { EntityName = "Payment" },
            original with { Description = "Different reason" }
        ];
        Assert.That(changes.Select(AuditEnvelopeContent.Fingerprint), Has.None.EqualTo(fingerprint));
        Assert.That(AuditEnvelopeContent.Fingerprint(original with { OccurredAt = original.OccurredAt.ToOffset(TimeSpan.FromHours(-5)) }), Is.EqualTo(fingerprint));
    }

    [Test]
    public void EntityCaptureTimeIsReconstructableAndPropertyOrderIsNotSemantic()
    {
        var original = Event() with
        {
            Kind = AuditEnvelopeKind.EntityChange, ExplicitEventId = null, EventType = null,
            PropertyChanges = [new("Amount", "1", "2"), new("Status", "Draft", "Approved")]
        };
        var retry = original with { OccurredAt = original.OccurredAt.AddHours(1), PropertyChanges = original.PropertyChanges!.Reverse().ToArray() };
        Assert.That(AuditEnvelopeContent.Fingerprint(retry), Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
        var changed = original with { PropertyChanges = [new("Amount", "1", "3"), new("Status", "Draft", "Approved")] };
        Assert.That(AuditEnvelopeContent.Fingerprint(changed), Is.Not.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [Test]
    public void SubjectOrderAndRepeatedIdenticalLinksDoNotChangeContent()
    {
        var first = new AuditSubjectReference(Guid.NewGuid(), "Owner");
        var second = new AuditSubjectReference(Guid.NewGuid(), "Customer");
        var original = Event() with { SubjectReferences = [first, second] };
        Assert.That(AuditEnvelopeContent.Fingerprint(original with { SubjectReferences = [second, first, first] }),
            Is.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
        Assert.That(AuditEnvelopeContent.Fingerprint(original with { SubjectReferences = [first] }),
            Is.Not.EqualTo(AuditEnvelopeContent.Fingerprint(original)));
    }

    [Test]
    public void VersionOneHasAStableReferenceDigest()
    {
        // Independent reference built with Python sorted compact JSON and hashlib.sha256.
        Assert.That(AuditEnvelopeContent.Version, Is.EqualTo(1));
        Assert.That(AuditEnvelopeContent.Fingerprint(Event()), Is.EqualTo("05FA94230EF994C00FBE2D7E016BBA9105FCB9A0B9315BCF4894228DA2892ADD"));
    }

    [Test]
    public void LocaleDoesNotChangeComparison()
    {
        var original = Event();
        var expected = AuditEnvelopeContent.Fingerprint(original);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
            Assert.That(AuditEnvelopeContent.Fingerprint(original), Is.EqualTo(expected));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [TestCase("{\"nested\":{\"x\":1,\"x\":2}}")]
    [TestCase("[{\"x\":1,\"x\":2}]")]
    public void RepeatedJsonPropertiesAtAnyDepthAreInvalid(string payload)
    {
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Fingerprint(Event() with { AdditionalData = payload }));
    }

    [Test]
    public void RepeatedJsonPropertiesRejectAmbiguousContent()
    {
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Fingerprint(Event() with { AdditionalData = "{\"a\":1,\"a\":2}" }));
    }

    [Test]
    public void MissingAndEmptyLogicalIdentityAreInvalid()
    {
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Validate(Event() with { ExplicitEventId = null }));
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Validate(Event() with { ExplicitEventId = Guid.Empty }));
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Validate(Event() with { EnvelopeId = Guid.Empty }));
    }

    [Test]
    public void RepeatedEnvelopeIdentityRejectsBeforeAnyBatchConsumerCanRun()
    {
        var original = Event();
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.ValidateBatch([original, original]));
        Assert.DoesNotThrow(() => AuditEnvelopeContent.ValidateBatch([original, original with { EnvelopeId = Guid.NewGuid() }]));
    }

    [Test]
    public void InvalidUtf16CannotCollapseIntoAValidReplacementCharacter()
    {
        var invalid = new string((char)0xD800, 1);
        var valid = Event() with { Description = "\uFFFD" };
        Assert.DoesNotThrow(() => AuditEnvelopeContent.Fingerprint(valid));
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Fingerprint(valid with { Description = invalid }));
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Fingerprint(valid with { PropertyChanges = [new("Amount", invalid, "2")] }));
    }

    [Test]
    public void RepeatedPropertyNamesAreInvalid()
    {
        Assert.Throws<ArgumentException>(() => AuditEnvelopeContent.Validate(Event() with { PropertyChanges = [new("Amount", "1", "2"), new("Amount", "1", "3")] }));
    }
}
