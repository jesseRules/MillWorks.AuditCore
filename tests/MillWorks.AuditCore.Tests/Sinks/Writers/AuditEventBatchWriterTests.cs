using MillWorks.AuditCore.Tests.Helpers;
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
    private Mock<IAuditEnvelopeBatchLogger> _envelopeLogger = null!;
    private AuditEventBatchWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _auditLogger = new Mock<IAuditLogger>();
        _envelopeLogger = _auditLogger.As<IAuditEnvelopeBatchLogger>();
        _writer = new AuditEventBatchWriter(
            _auditLogger.Object,
            NullLogger<AuditEventBatchWriter>.Instance);
    }

    [TestCase("aggregate")]
    [TestCase("aggregate_exception")]
    [TestCase("missing")]
    [TestCase("duplicate")]
    [TestCase("unknown")]
    [TestCase("unrecognized")]
    [TestCase("contradictory")]
    public async Task MalformedExactResultsAreContractDefects(string defect)
    {
        var first = new AuditEnvelope { Kind = AuditEnvelopeKind.ExplicitEvent, Action = AuditAction.Unknown, ExplicitEventId = Guid.NewGuid(), EntityName = "Test", EventType = "Test" };
        var second = first with { EnvelopeId = Guid.NewGuid(), ExplicitEventId = Guid.NewGuid() };
        var one = new AuditEventOutcome(first.ExplicitEventId!.Value, AuditEventWriteStatus.Persisted);
        var two = new AuditEventOutcome(second.ExplicitEventId!.Value, AuditEventWriteStatus.Persisted);
        _envelopeLogger.Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BatchAuditResult { Success = true, EventCount = 2, Exception = defect == "aggregate_exception" ? new Exception("contradictory") : null, EventOutcomes = defect switch
            {
                "aggregate" => null, "aggregate_exception" => [one, two], "missing" => [one], "duplicate" => [one, one],
                "unknown" => [one, two with { EventId = Guid.NewGuid() }],
                "unrecognized" => [one, two with { Status = (AuditEventWriteStatus)999 }],
                _ => [one, two with { IsRetryable = true }]
            }});
        await Assert.ThrowsAsync<MillWorks.AuditCore.Services.Sinks.Processing.AuditBatchContractException>(
            () => _writer.WriteBatchAsync([first, second], default));
    }

    [Test]
    public async Task WriteBatchAsync_NullEnvelopes_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _writer.WriteBatchAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task WriteBatchAsync_EmptyList_ReturnsEmptyOutcomes()
    {
        var outcomes = await _writer.WriteBatchAsync([], CancellationToken.None);

        Assert.That(outcomes, Is.Empty);
        _envelopeLogger.VerifyNoOtherCalls();
    }

    [Test]
    public async Task WriteBatchAsync_SingleEnvelope_CallsLogEnvelopeBatchAsync()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Succeeded(1)));

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].EnvelopeId, Is.EqualTo(envelope.EnvelopeId));
        Assert.That(outcomes[0].Succeeded, Is.True);

        _envelopeLogger.Verify(
            l => l.LogEnvelopeBatchAsync(It.Is<IReadOnlyList<AuditEnvelope>>(e => e.Count == 1), It.IsAny<CancellationToken>()),
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
                ExplicitEventId = Guid.NewGuid(),
                EntityName = "User.Login",
                Action = AuditAction.Unknown,
                EventType = "User.Login",
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                ExplicitEventId = Guid.NewGuid(),
                EntityName = "User.Logout",
                Action = AuditAction.Unknown,
                EventType = "User.Logout",
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                ExplicitEventId = Guid.NewGuid(),
                EntityName = "User.PasswordChange",
                Action = AuditAction.Unknown,
                EventType = "User.PasswordChange",
            },
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Succeeded(3)));

        var outcomes = await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(3));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);

        var outcomeIds = outcomes.Select(o => o.EnvelopeId).ToHashSet();
        var envelopeIds = envelopes.Select(e => e.EnvelopeId).ToHashSet();
        Assert.That(outcomeIds, Is.EquivalentTo(envelopeIds));

        _envelopeLogger.Verify(
            l => l.LogEnvelopeBatchAsync(It.Is<IReadOnlyList<AuditEnvelope>>(e => e.Count == 3), It.IsAny<CancellationToken>()),
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
            ExplicitEventId = Guid.NewGuid(),
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
        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEnvelope>, CancellationToken>((events, _) => capturedEvent = events.Select(AuditEventBatchWriter.MapToAuditEvent).FirstOrDefault())
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Succeeded(1)));

        await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(capturedEvent!.EventId, Is.EqualTo(envelope.ExplicitEventId));
            Assert.That(capturedEvent.EventType, Is.EqualTo("User.Login"));
            Assert.That(capturedEvent.EntityName, Is.EqualTo("User.Login"));
            Assert.That(capturedEvent.Action, Is.EqualTo(AuditAction.Unknown));
            Assert.That(capturedEvent.StartDate, Is.EqualTo(occurredAt));
            Assert.That(capturedEvent.AspNetUserId, Is.EqualTo("alice"));
            Assert.That(capturedEvent.CorrelationId, Is.EqualTo("corr-123"));
            Assert.That(capturedEvent.IpAddress, Is.EqualTo("10.0.0.1"));
            Assert.That(capturedEvent.UserAgent, Is.EqualTo("Mozilla/5.0"));
            Assert.That(capturedEvent.KeyValues["Id"], Is.EqualTo(entityId));
            Assert.That(capturedEvent.CustomFields["EntityId"], Is.EqualTo(entityId.ToString("D")));
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
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
        };

        AuditEvent? capturedEvent = null;
        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEnvelope>, CancellationToken>((events, _) => capturedEvent = events.Select(AuditEventBatchWriter.MapToAuditEvent).FirstOrDefault())
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Succeeded(1)));

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
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Event1",
            Action = AuditAction.Unknown,
            EventType = "Event1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Event2",
            Action = AuditAction.Unknown,
            EventType = "Event2",
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> events, CancellationToken _) =>
                TestEnvelopeBatchResults.Create(events, BatchAuditResult.Failed(events.Select(AuditEventBatchWriter.MapToAuditEvent).ToList(), new InvalidOperationException("DB connection lost"))));

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
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Event1",
            Action = AuditAction.Unknown,
            EventType = "Event1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Event2",
            Action = AuditAction.Unknown,
            EventType = "Event2",
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Failed([], new InvalidOperationException("Connection refused"))));

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
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Success1",
            Action = AuditAction.Unknown,
            EventType = "Success1",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Failed1",
            Action = AuditAction.Unknown,
            EventType = "Failed1",
        };
        var envelope3 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "Success2",
            Action = AuditAction.Unknown,
            EventType = "Success2",
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEnvelope>, CancellationToken>((e, _) => events.AddRange(e.Select(AuditEventBatchWriter.MapToAuditEvent)))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> capturedEvents, CancellationToken _) =>
            {
                var failed = capturedEvents.Where(e => e.EventType == "Failed1").Select(AuditEventBatchWriter.MapToAuditEvent).ToList();
                return TestEnvelopeBatchResults.Create(capturedEvents, BatchAuditResult.Failed(failed, new InvalidOperationException("Partial failure")));
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
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "A",
            Action = AuditAction.Unknown,
            EventType = "A",
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            ExplicitEventId = Guid.NewGuid(),
            EntityName = "B",
            Action = AuditAction.Unknown,
            EventType = "B",
        };

        _envelopeLogger
            .Setup(l => l.LogEnvelopeBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> input, CancellationToken _) => TestEnvelopeBatchResults.Create(input, BatchAuditResult.Succeeded(2)));

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2], CancellationToken.None);

        Assert.That(outcomes.Any(o => o.EnvelopeId == envelope1.EnvelopeId), Is.True);
        Assert.That(outcomes.Any(o => o.EnvelopeId == envelope2.EnvelopeId), Is.True);
    }
}
