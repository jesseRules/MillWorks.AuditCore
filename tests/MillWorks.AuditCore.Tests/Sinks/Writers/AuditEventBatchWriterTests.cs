using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks.Writers;
using Moq;

namespace MillWorks.AuditCore.Tests.Sinks.Writers;

[TestFixture]
[Category("Unit")]
public sealed class AuditEventBatchWriterTests
{
    private Mock<IAuditLogger> _auditLogger = null!;
    private AuditEventBatchWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _auditLogger = new Mock<IAuditLogger>();
        _writer = new AuditEventBatchWriter(
            _auditLogger.Object,
            NullLogger<AuditEventBatchWriter>.Instance);
    }

    [Test]
    public void WriteBatchAsync_NullEnvelopes_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _writer.WriteBatchAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task WriteBatchAsync_EmptyList_ReturnsEmptyOutcomes()
    {
        var outcomes = await _writer.WriteBatchAsync([], CancellationToken.None);

        Assert.That(outcomes, Is.Empty);
        _auditLogger.VerifyNoOtherCalls();
    }

    [Test]
    public async Task WriteBatchAsync_SingleEnvelope_CallsLogBatchAsync()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchAuditResult.Succeeded(1));

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].EnvelopeId, Is.EqualTo(envelope.EnvelopeId));
        Assert.That(outcomes[0].Succeeded, Is.True);

        _auditLogger.Verify(
            l => l.LogBatchAsync(It.Is<IReadOnlyList<AuditEvent>>(e => e.Count == 1), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WriteBatchAsync_MultipleEnvelopes_BatchesToSingleCall()
    {
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.Login",
                Action = AuditAction.Unknown,
                EventType = "User.Login",
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.Logout",
                Action = AuditAction.Unknown,
                EventType = "User.Logout",
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.PasswordChange",
                Action = AuditAction.Unknown,
                EventType = "User.PasswordChange",
            },
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchAuditResult.Succeeded(3));

        var outcomes = await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(3));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);

        var outcomeIds = outcomes.Select(o => o.EnvelopeId).ToHashSet();
        var envelopeIds = envelopes.Select(e => e.EnvelopeId).ToHashSet();
        Assert.That(outcomeIds, Is.EquivalentTo(envelopeIds));

        _auditLogger.Verify(
            l => l.LogBatchAsync(It.Is<IReadOnlyList<AuditEvent>>(e => e.Count == 3), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task WriteBatchAsync_MapsEnvelopeToAuditEvent()
    {
        var occurredAt = new DateTimeOffset(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);
        var entityId = Guid.NewGuid();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
            OccurredAt = occurredAt,
            UserId = "alice",
            CorrelationId = "corr-123",
            IpAddress = "10.0.0.1",
            UserAgent = "Mozilla/5.0",
            Description = "Login OK",
            AdditionalData = "{\"method\":\"oauth\"}",
            EntityId = entityId,
        };

        AuditEvent? capturedEvent = null;
        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEvent>, CancellationToken>((events, _) => capturedEvent = events.FirstOrDefault())
            .ReturnsAsync(BatchAuditResult.Succeeded(1));

        await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedEvent!.EventType, Is.EqualTo("User.Login"));
            Assert.That(capturedEvent.EntityName, Is.EqualTo("User.Login"));
            Assert.That(capturedEvent.Action, Is.EqualTo(AuditAction.Unknown));
            Assert.That(capturedEvent.StartDate, Is.EqualTo(occurredAt));
            Assert.That(capturedEvent.AspNetUserId, Is.EqualTo("alice"));
            Assert.That(capturedEvent.CorrelationId, Is.EqualTo("corr-123"));
            Assert.That(capturedEvent.IpAddress, Is.EqualTo("10.0.0.1"));
            Assert.That(capturedEvent.UserAgent, Is.EqualTo("Mozilla/5.0"));
            Assert.That(capturedEvent.KeyValues["Id"], Is.EqualTo(entityId));
            Assert.That(capturedEvent.CustomFields["Description"], Is.EqualTo("Login OK"));
            Assert.That(capturedEvent.CustomFields["AdditionalData"], Is.EqualTo("{\"method\":\"oauth\"}"));
        });
    }

    [Test]
    public async Task WriteBatchAsync_OmitsNullOptionalFields()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
        };

        AuditEvent? capturedEvent = null;
        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEvent>, CancellationToken>((events, _) => capturedEvent = events.FirstOrDefault())
            .ReturnsAsync(BatchAuditResult.Succeeded(1));

        await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.KeyValues, Is.Empty);
        Assert.That(capturedEvent.CustomFields, Is.Empty);
    }

    [Test]
    public async Task WriteBatchAsync_BatchFailure_ReturnsFailedOutcomes()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Event1",
            Action = AuditAction.Unknown,
            EventType = "Event1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Event2",
            Action = AuditAction.Unknown,
            EventType = "Event2",
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEvent> events, CancellationToken _) =>
                BatchAuditResult.Failed(events.ToList(), new InvalidOperationException("DB connection lost")));

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(2));
        Assert.That(outcomes.All(o => !o.Succeeded), Is.True);
        Assert.That(outcomes.All(o => o.IsRetryable), Is.True);
        Assert.That(outcomes.All(o => o.ErrorMessage == "DB connection lost"), Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_TotalFailureWithEmptyFailedEvents_MarksAllAsFailed()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Event1",
            Action = AuditAction.Unknown,
            EventType = "Event1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Event2",
            Action = AuditAction.Unknown,
            EventType = "Event2",
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchAuditResult.Failed([], new InvalidOperationException("Connection refused")));

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(2));
        Assert.That(outcomes.All(o => !o.Succeeded), Is.True, "All events should fail when FailedEvents is empty");
        Assert.That(outcomes.All(o => o.IsRetryable), Is.True);
        Assert.That(outcomes.All(o => o.ErrorMessage == "Connection refused"), Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_PartialFailure_CorrectlyMapsOutcomes()
    {
        var events = new List<AuditEvent>();

        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Success1",
            Action = AuditAction.Unknown,
            EventType = "Success1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Failed1",
            Action = AuditAction.Unknown,
            EventType = "Failed1",
        };
        var envelope3 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Success2",
            Action = AuditAction.Unknown,
            EventType = "Success2",
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEvent>, CancellationToken>((e, _) => events.AddRange(e))
            .ReturnsAsync((IReadOnlyList<AuditEvent> capturedEvents, CancellationToken _) =>
            {
                var failed = capturedEvents.Where(e => e.EventType == "Failed1").ToList();
                return BatchAuditResult.Failed(failed, new InvalidOperationException("Partial failure"));
            });

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2, envelope3], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(3));

        var outcome1 = outcomes.Single(o => o.EnvelopeId == envelope1.EnvelopeId);
        var outcome2 = outcomes.Single(o => o.EnvelopeId == envelope2.EnvelopeId);
        var outcome3 = outcomes.Single(o => o.EnvelopeId == envelope3.EnvelopeId);

        Assert.That(outcome1.Succeeded, Is.True);
        Assert.That(outcome2.Succeeded, Is.False);
        Assert.That(outcome3.Succeeded, Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_OutcomesCorrelateByEnvelopeId()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "A",
            Action = AuditAction.Unknown,
            EventType = "A",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "B",
            Action = AuditAction.Unknown,
            EventType = "B",
        };

        _auditLogger
            .Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(BatchAuditResult.Succeeded(2));

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2], CancellationToken.None);

        Assert.That(outcomes.Any(o => o.EnvelopeId == envelope1.EnvelopeId), Is.True);
        Assert.That(outcomes.Any(o => o.EnvelopeId == envelope2.EnvelopeId), Is.True);
    }
}
