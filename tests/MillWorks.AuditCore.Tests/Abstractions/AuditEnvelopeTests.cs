using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Tests.Abstractions;

[TestFixture]
[Category("Unit")]
public sealed class AuditEnvelopeTests
{
    [Test]
    public void Construct_EntityChange_WithMinimumRequiredFields()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
        };

        Assert.That(envelope.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
        Assert.That(envelope.EntityName, Is.EqualTo("Patient"));
        Assert.That(envelope.Action, Is.EqualTo(AuditAction.Updated));
        Assert.That(envelope.PropertyChanges, Is.Null);
        Assert.That(envelope.EventType, Is.Null);
        Assert.That(envelope.AdditionalData, Is.Null);
        Assert.That(envelope.Description, Is.Null);
    }

    [Test]
    public void Construct_ExplicitEvent_WithMinimumRequiredFields()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
        };

        Assert.That(envelope.Kind, Is.EqualTo(AuditEnvelopeKind.ExplicitEvent));
        Assert.That(envelope.EntityName, Is.EqualTo("User.Login"));
        Assert.That(envelope.Action, Is.EqualTo(AuditAction.Unknown));
        Assert.That(envelope.PropertyChanges, Is.Null);
    }

    [Test]
    public void Construct_DefaultsOccurredAtToNow()
    {
        var before = DateTimeOffset.UtcNow;

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };

        var after = DateTimeOffset.UtcNow;

        Assert.That(envelope.OccurredAt, Is.GreaterThanOrEqualTo(before));
        Assert.That(envelope.OccurredAt, Is.LessThanOrEqualTo(after));
    }

    [Test]
    public void Construct_EntityChange_WithPropertyChanges()
    {
        var changes = new List<AuditEnvelopePropertyChange>
        {
            new("Status", "Pending", "Active"),
            new("UpdatedAt", null, "2026-04-26T00:00:00Z"),
        };

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            PropertyChanges = changes,
        };

        Assert.That(envelope.PropertyChanges, Is.Not.Null);
        Assert.That(envelope.PropertyChanges, Has.Count.EqualTo(2));
        Assert.That(envelope.PropertyChanges![0].PropertyName, Is.EqualTo("Status"));
        Assert.That(envelope.PropertyChanges[0].OldValue, Is.EqualTo("Pending"));
        Assert.That(envelope.PropertyChanges[0].NewValue, Is.EqualTo("Active"));
    }

    [Test]
    public void Construct_ExplicitEvent_WithEventTypeAndPayload()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
            AdditionalData = "{\"method\":\"oauth\"}",
            UserId = "user-123",
            CorrelationId = "corr-abc",
            IpAddress = "10.0.0.1",
            UserAgent = "ua",
            Description = "Successful login",
        };

        Assert.That(envelope.EventType, Is.EqualTo("User.Login"));
        Assert.That(envelope.AdditionalData, Is.EqualTo("{\"method\":\"oauth\"}"));
        Assert.That(envelope.UserId, Is.EqualTo("user-123"));
        Assert.That(envelope.CorrelationId, Is.EqualTo("corr-abc"));
        Assert.That(envelope.IpAddress, Is.EqualTo("10.0.0.1"));
        Assert.That(envelope.UserAgent, Is.EqualTo("ua"));
        Assert.That(envelope.Description, Is.EqualTo("Successful login"));
    }

    [Test]
    public void With_ProducesNewInstance_OriginalUnchanged()
    {
        var original = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            UserId = "alice",
        };

        var modified = original with { UserId = "bob" };

        Assert.That(modified, Is.Not.SameAs(original));
        Assert.That(original.UserId, Is.EqualTo("alice"));
        Assert.That(modified.UserId, Is.EqualTo("bob"));
        Assert.That(modified.EntityName, Is.EqualTo("Patient"));
        Assert.That(modified.Action, Is.EqualTo(AuditAction.Created));
    }

    [Test]
    public void Equality_TwoEnvelopesWithIdenticalValues_AreEqual()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var envelopeId = Guid.NewGuid();

        var a = new AuditEnvelope
        {
            EnvelopeId = envelopeId,
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            OccurredAt = occurredAt,
        };

        var b = new AuditEnvelope
        {
            EnvelopeId = envelopeId,
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            OccurredAt = occurredAt,
        };

        Assert.That(a, Is.EqualTo(b));
        Assert.That(a.GetHashCode(), Is.EqualTo(b.GetHashCode()));
    }

    [Test]
    public void PropertyChange_HoldsAllThreeFields()
    {
        var change = new AuditEnvelopePropertyChange("Email", "old@x.com", "new@x.com");

        Assert.That(change.PropertyName, Is.EqualTo("Email"));
        Assert.That(change.OldValue, Is.EqualTo("old@x.com"));
        Assert.That(change.NewValue, Is.EqualTo("new@x.com"));
    }

    [Test]
    public void PropertyChange_AllowsNullOldAndNewValues()
    {
        var added = new AuditEnvelopePropertyChange("Name", OldValue: null, NewValue: "Alice");
        var deleted = new AuditEnvelopePropertyChange("Name", OldValue: "Alice", NewValue: null);

        Assert.That(added.OldValue, Is.Null);
        Assert.That(added.NewValue, Is.EqualTo("Alice"));
        Assert.That(deleted.OldValue, Is.EqualTo("Alice"));
        Assert.That(deleted.NewValue, Is.Null);
    }

    [Test]
    public void EnvelopeId_AutoGeneratesUniqueGuid()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };

        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };

        Assert.That(envelope1.EnvelopeId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(envelope2.EnvelopeId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(envelope1.EnvelopeId, Is.Not.EqualTo(envelope2.EnvelopeId));
    }

    [Test]
    public void EnvelopeId_CanBeExplicitlySet()
    {
        var specificId = Guid.NewGuid();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            EnvelopeId = specificId,
        };

        Assert.That(envelope.EnvelopeId, Is.EqualTo(specificId));
    }

    [Test]
    public void EnvelopeId_SurvivesJsonRoundTrip()
    {
        var original = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            UserId = "user-123",
            CorrelationId = "corr-abc",
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(original, options);
        var deserialized = JsonSerializer.Deserialize<AuditEnvelope>(json, options);

        Assert.That(deserialized, Is.Not.Null);
        Assert.That(deserialized!.EnvelopeId, Is.EqualTo(original.EnvelopeId));
    }

    [Test]
    public void EnvelopeId_JsonContainsCamelCaseProperty()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        var json = JsonSerializer.Serialize(envelope, options);

        Assert.That(json, Does.Contain("\"envelopeId\""));
        Assert.That(json, Does.Contain(envelope.EnvelopeId.ToString()));
    }
}
