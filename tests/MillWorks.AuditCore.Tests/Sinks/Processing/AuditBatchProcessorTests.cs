using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Writers;
using Moq;

namespace MillWorks.AuditCore.Tests.Sinks.Processing;

[TestFixture]
[Category("Unit")]
public sealed class AuditBatchProcessorTests
{
    private Mock<IAuditEntityBatchWriter> _entityWriterMock = null!;
    private Mock<IAuditEventBatchWriter> _eventWriterMock = null!;
    private AuditBatchProcessor _processor = null!;
    private static readonly DateTimeOffset TestTime = new(2026, 5, 19, 12, 0, 0, TimeSpan.Zero);

    [SetUp]
    public void SetUp()
    {
        _entityWriterMock = new Mock<IAuditEntityBatchWriter>();
        _eventWriterMock = new Mock<IAuditEventBatchWriter>();

        _processor = new AuditBatchProcessor(
            _entityWriterMock.Object,
            _eventWriterMock.Object,
            TimeProvider.System,
            NullLogger<AuditBatchProcessor>.Instance);
    }

    [Test]
    public async Task ProcessBatchAsync_NullRows_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ProcessBatchAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task ProcessBatchAsync_EmptyList_ReturnsEmptyOutcomes()
    {
        var result = await _processor.ProcessBatchAsync([], CancellationToken.None);

        Assert.That(result.Outcomes, Is.Empty);
        _entityWriterMock.VerifyNoOtherCalls();
        _eventWriterMock.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ProcessBatchAsync_EntityChangeEnvelopes_RoutesToEntityWriter()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = Guid.NewGuid(), Envelope = envelope, AttemptCount = 0, CreatedAt = TestTime }
        };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(envelope.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        _entityWriterMock.Verify(w => w.WriteBatchAsync(
            It.Is<IReadOnlyList<AuditEnvelope>>(list => list.Count == 1 && list[0].EnvelopeId == envelope.EnvelopeId),
            It.IsAny<CancellationToken>()), Times.Once);
        _eventWriterMock.VerifyNoOtherCalls();
        Assert.That(result.Outcomes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ProcessBatchAsync_ExplicitEventEnvelopes_RoutesToEventWriter()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "UserLogin",
        };
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = Guid.NewGuid(), Envelope = envelope, AttemptCount = 0, CreatedAt = TestTime }
        };

        _eventWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(envelope.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        _eventWriterMock.Verify(w => w.WriteBatchAsync(
            It.Is<IReadOnlyList<AuditEnvelope>>(list => list.Count == 1 && list[0].EnvelopeId == envelope.EnvelopeId),
            It.IsAny<CancellationToken>()), Times.Once);
        _entityWriterMock.VerifyNoOtherCalls();
        Assert.That(result.Outcomes, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task ProcessBatchAsync_MixedEnvelopes_RoutesToBothWriters()
    {
        var entityEnvelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
        };
        var eventEnvelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "Export",
            Action = AuditAction.Unknown,
            EventType = "DataExport",
        };
        var entityRow = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = entityEnvelope, AttemptCount = 0, CreatedAt = TestTime };
        var eventRow = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = eventEnvelope, AttemptCount = 1, CreatedAt = TestTime };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(entityEnvelope.EnvelopeId)]);
        _eventWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(eventEnvelope.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync([entityRow, eventRow], CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(2));
        _entityWriterMock.Verify(w => w.WriteBatchAsync(
            It.Is<IReadOnlyList<AuditEnvelope>>(list => list.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _eventWriterMock.Verify(w => w.WriteBatchAsync(
            It.Is<IReadOnlyList<AuditEnvelope>>(list => list.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessBatchAsync_WriteOutcomeSuccess_MapsToRowStatusSucceeded()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };
        var rowId = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId, Envelope = envelope, AttemptCount = 0, CreatedAt = TestTime }
        };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(envelope.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.RowId, Is.EqualTo(rowId));
            Assert.That(outcome.Status, Is.EqualTo(RowStatus.Succeeded));
            Assert.That(outcome.ErrorMessage, Is.Null);
            Assert.That(outcome.IsRetryable, Is.False);
        });
    }

    [Test]
    public async Task ProcessBatchAsync_WriteOutcomeDuplicate_MapsToRowStatusDuplicate()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "Login",
        };
        var rowId = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId, Envelope = envelope, AttemptCount = 1, CreatedAt = TestTime }
        };

        _eventWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Duplicate(envelope.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.RowId, Is.EqualTo(rowId));
            Assert.That(outcome.Status, Is.EqualTo(RowStatus.Duplicate));
            Assert.That(outcome.ErrorMessage, Is.Null);
            Assert.That(outcome.IsRetryable, Is.False);
        });
    }

    [Test]
    public async Task ProcessBatchAsync_WriteOutcomeFailedRetryable_MapsToRowStatusRetryLater()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Visit",
            Action = AuditAction.Updated,
        };
        var rowId = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId, Envelope = envelope, AttemptCount = 2, CreatedAt = TestTime }
        };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Failed(envelope.EnvelopeId, "Deadlock", isRetryable: true)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.RowId, Is.EqualTo(rowId));
            Assert.That(outcome.Status, Is.EqualTo(RowStatus.RetryLater));
            Assert.That(outcome.ErrorMessage, Is.EqualTo("Deadlock"));
            Assert.That(outcome.IsRetryable, Is.True);
        });
    }

    [Test]
    public async Task ProcessBatchAsync_WriteOutcomeFailedNonRetryable_MapsToRowStatusFailed()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Appointment",
            Action = AuditAction.Deleted,
        };
        var rowId = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId, Envelope = envelope, AttemptCount = 3, CreatedAt = TestTime }
        };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Failed(envelope.EnvelopeId, "Constraint violation", isRetryable: false)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.RowId, Is.EqualTo(rowId));
            Assert.That(outcome.Status, Is.EqualTo(RowStatus.Failed));
            Assert.That(outcome.ErrorMessage, Is.EqualTo("Constraint violation"));
            Assert.That(outcome.IsRetryable, Is.False);
        });
    }

    [Test]
    public async Task ProcessBatchAsync_CombinesOutcomesFromBothWriters()
    {
        var entity1 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created };
        var entity2 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "B", Action = AuditAction.Updated };
        var event1 = new AuditEnvelope { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "Audit.Event", Action = AuditAction.Unknown, EventType = "X" };

        var row1 = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = entity1, AttemptCount = 0, CreatedAt = TestTime };
        var row2 = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = entity2, AttemptCount = 0, CreatedAt = TestTime };
        var row3 = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = event1, AttemptCount = 0, CreatedAt = TestTime };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(entity1.EnvelopeId), WriteOutcome.Duplicate(entity2.EnvelopeId)]);
        _eventWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(event1.EnvelopeId)]);

        var result = await _processor.ProcessBatchAsync([row1, row2, row3], CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(3));
        var rowIdStatuses = result.Outcomes.ToDictionary(o => o.RowId, o => o.Status);
        Assert.Multiple(() =>
        {
            Assert.That(rowIdStatuses[row1.RowId], Is.EqualTo(RowStatus.Succeeded));
            Assert.That(rowIdStatuses[row2.RowId], Is.EqualTo(RowStatus.Duplicate));
            Assert.That(rowIdStatuses[row3.RowId], Is.EqualTo(RowStatus.Succeeded));
        });
    }

    [Test]
    public async Task ProcessBatchAsync_WriterThrows_MarksAllRowsAsRetryable()
    {
        var envelope1 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created };
        var envelope2 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "B", Action = AuditAction.Updated };

        var row1 = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = envelope1, AttemptCount = 0, CreatedAt = TestTime };
        var row2 = new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = envelope2, AttemptCount = 1, CreatedAt = TestTime };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database connection lost"));

        var result = await _processor.ProcessBatchAsync([row1, row2], CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(2));
        Assert.That(result.Outcomes.All(o => o.Status == RowStatus.RetryLater), Is.True);
        Assert.That(result.Outcomes.All(o => o.ErrorMessage!.Contains("Database connection lost")), Is.True);
        Assert.That(result.Outcomes.All(o => o.IsRetryable), Is.True);
    }

    [Test]
    public async Task ProcessBatchAsync_MultipleEntityChanges_AllRoutedToSingleWriterCall()
    {
        var envelopes = Enumerable.Range(0, 5)
            .Select(i => new AuditEnvelope
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = $"Entity{i}",
                Action = AuditAction.Created,
            })
            .ToList();
        var rows = envelopes.Select(e =>
            new ClaimedOutboxRow { RowId = Guid.NewGuid(), Envelope = e, AttemptCount = 0, CreatedAt = TestTime }).ToList();

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        _entityWriterMock.Verify(w => w.WriteBatchAsync(
            It.Is<IReadOnlyList<AuditEnvelope>>(list => list.Count == 5),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.That(result.Outcomes, Has.Count.EqualTo(5));
        Assert.That(result.Outcomes.All(o => o.Status == RowStatus.Succeeded), Is.True);
    }

    [Test]
    public async Task ProcessBatchAsync_UnknownEnvelopeKind_ReturnsFailed()
    {
        var envelope = new AuditEnvelope
        {
            Kind = (AuditEnvelopeKind)999,
            EntityName = "Unknown",
            Action = AuditAction.Unknown,
        };
        var rowId = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId, Envelope = envelope, AttemptCount = 0, CreatedAt = TestTime }
        };

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome = result.Outcomes.Single();
        Assert.Multiple(() =>
        {
            Assert.That(outcome.RowId, Is.EqualTo(rowId));
            Assert.That(outcome.Status, Is.EqualTo(RowStatus.Failed));
            Assert.That(outcome.ErrorMessage, Does.Contain("Unhandled AuditEnvelopeKind"));
            Assert.That(outcome.IsRetryable, Is.False);
        });

        _entityWriterMock.VerifyNoOtherCalls();
        _eventWriterMock.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ProcessBatchAsync_RowIdCorrelationPreserved()
    {
        var envelope1 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created };
        var envelope2 = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "B", Action = AuditAction.Updated };

        var rowId1 = Guid.NewGuid();
        var rowId2 = Guid.NewGuid();
        var rows = new List<ClaimedOutboxRow>
        {
            new() { RowId = rowId1, Envelope = envelope1, AttemptCount = 0, CreatedAt = TestTime },
            new() { RowId = rowId2, Envelope = envelope2, AttemptCount = 0, CreatedAt = TestTime }
        };

        _entityWriterMock
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([WriteOutcome.Success(envelope1.EnvelopeId), WriteOutcome.Failed(envelope2.EnvelopeId, "Error", isRetryable: true)]);

        var result = await _processor.ProcessBatchAsync(rows, CancellationToken.None);

        var outcome1 = result.Outcomes.Single(o => o.RowId == rowId1);
        var outcome2 = result.Outcomes.Single(o => o.RowId == rowId2);

        Assert.Multiple(() =>
        {
            Assert.That(outcome1.Status, Is.EqualTo(RowStatus.Succeeded));
            Assert.That(outcome2.Status, Is.EqualTo(RowStatus.RetryLater));
        });
    }
}
