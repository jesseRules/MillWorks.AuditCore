using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Exceptions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Processing;
using MillWorks.AuditCore.Services.Sinks.Writers;
using Moq;

namespace MillWorks.AuditCore.Tests.Sinks;

/// <summary>
/// Tests for security fixes:
/// 1. ImmediateSink throws on write failures instead of swallowing them
/// 2. TransactionalOutboxSink uses ExplicitEventId for explicit events
/// 3. AuditBatchProcessor verifies writer output cardinality
/// 4. AuditOutboxWriter validates schema name
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class SinkSecurityFixesTests
{
    #region Finding 1: ImmediateSink throws on write failures

    [Test]
    public async Task ImmediateSink_AllSuccess_DoesNotThrow()
    {
        var entityWriter = CreateSuccessEntityWriter();
        var eventWriter = CreateSuccessEventWriter();
        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "Patient", Action = AuditAction.Created }
        };

        await sink.PublishBatchAsync(envelopes);
    }

    [Test]
    public void ImmediateSink_EntityWriterFailure_ThrowsAuditWriteException()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Failed(e.EnvelopeId, "DB connection lost", isRetryable: true)).ToList());

        var eventWriter = CreateSuccessEventWriter();
        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "Patient", Action = AuditAction.Created }
        };

        var ex = Assert.ThrowsAsync<AuditWriteException>(() => sink.PublishBatchAsync(envelopes));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.FailedCount, Is.EqualTo(1));
            Assert.That(ex.TotalCount, Is.EqualTo(1));
            Assert.That(ex.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
            Assert.That(ex.Message, Does.Contain("DB connection lost"));
        });
    }

    [Test]
    public void ImmediateSink_EventWriterFailure_ThrowsAuditWriteException()
    {
        var entityWriter = CreateSuccessEntityWriter();
        var eventWriter = new Mock<IAuditEventBatchWriter>();
        eventWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Failed(e.EnvelopeId, "Constraint violation")).ToList());

        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "User.Login", Action = AuditAction.Unknown, EventType = "Login" }
        };

        var ex = Assert.ThrowsAsync<AuditWriteException>(() => sink.PublishBatchAsync(envelopes));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.FailedCount, Is.EqualTo(1));
            Assert.That(ex.Kind, Is.EqualTo(AuditEnvelopeKind.ExplicitEvent));
        });
    }

    [Test]
    public void ImmediateSink_PartialFailure_ThrowsWithCorrectCounts()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
            {
                var outcomes = new List<WriteOutcome>();
                for (int i = 0; i < envelopes.Count; i++)
                {
                    outcomes.Add(i % 2 == 0
                        ? WriteOutcome.Success(envelopes[i].EnvelopeId)
                        : WriteOutcome.Failed(envelopes[i].EnvelopeId, "Intermittent failure"));
                }
                return outcomes;
            });

        var eventWriter = CreateSuccessEventWriter();
        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = Enumerable.Range(0, 4)
            .Select(i => new AuditEnvelope
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = $"Entity{i}",
                Action = AuditAction.Created
            })
            .ToList();

        var ex = Assert.ThrowsAsync<AuditWriteException>(() => sink.PublishBatchAsync(envelopes));
        Assert.Multiple(() =>
        {
            Assert.That(ex!.FailedCount, Is.EqualTo(2));
            Assert.That(ex.TotalCount, Is.EqualTo(4));
            Assert.That(ex.FailedEnvelopeIds, Has.Count.EqualTo(2));
        });
    }

    [Test]
    public void ImmediateSink_MixedKindsFailure_KindIsNull()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Failed(e.EnvelopeId, "Failed")).ToList());

        var eventWriter = new Mock<IAuditEventBatchWriter>();
        eventWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Failed(e.EnvelopeId, "Failed")).ToList());

        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "Patient", Action = AuditAction.Created },
            new() { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "User.Login", Action = AuditAction.Unknown, EventType = "Login" }
        };

        var ex = Assert.ThrowsAsync<AuditWriteException>(() => sink.PublishBatchAsync(envelopes));
        Assert.That(ex!.Kind, Is.Null);
    }

    [Test]
    public async Task ImmediateSink_DuplicateOutcome_TreatedAsSuccess()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Duplicate(e.EnvelopeId)).ToList());

        var eventWriter = CreateSuccessEventWriter();
        var sink = new ImmediateSink(entityWriter.Object, eventWriter.Object, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "Patient", Action = AuditAction.Created }
        };

        await sink.PublishBatchAsync(envelopes);
    }

    #endregion

    #region Finding 2: TransactionalOutboxSink uses ExplicitEventId

    [Test]
    public void ExtractIdempotencyKey_ExplicitEventWithExplicitEventId_ReturnsExplicitEventId()
    {
        var explicitEventId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "Login",
            ExplicitEventId = explicitEventId
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(explicitEventId));
        Assert.That(key, Is.Not.EqualTo(envelope.EnvelopeId));
    }

    [Test]
    public void ExtractIdempotencyKey_ExplicitEventWithoutExplicitEventId_FallsBackToEnvelopeId()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "Login"
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(envelope.EnvelopeId));
    }

    [Test]
    public void ExtractIdempotencyKey_EntityChange_AlwaysReturnsEnvelopeId()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(envelope.EnvelopeId));
    }

    [Test]
    public void ExtractIdempotencyKey_EntityChangeWithExplicitEventId_IgnoresExplicitEventId()
    {
        var explicitEventId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            ExplicitEventId = explicitEventId
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(envelope.EnvelopeId));
        Assert.That(key, Is.Not.EqualTo(explicitEventId));
    }

    [Test]
    public async Task TransactionalOutboxSink_ExplicitEventWithId_UsesCorrectIdempotencyKey()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        var explicitEventId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "Login",
            ExplicitEventId = explicitEventId
        };

        await sink.PublishAsync(envelope);

        Assert.That(writer.LastIdempotencyKey, Is.EqualTo(explicitEventId));
    }

    #endregion

    #region Finding 3: AuditBatchProcessor verifies cardinality

    [Test]
    public async Task AuditBatchProcessor_WriterReturnsTooFewOutcomes_FailsMissingRows()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
            {
                return new List<WriteOutcome> { WriteOutcome.Success(envelopes[0].EnvelopeId) };
            });

        var eventWriter = new Mock<IAuditEventBatchWriter>();
        var processor = new AuditBatchProcessor(
            entityWriter.Object, eventWriter.Object, TimeProvider.System, NullLogger<AuditBatchProcessor>.Instance);

        var row1 = new ClaimedOutboxRow
        {
            RowId = Guid.NewGuid(),
            Envelope = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created },
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var row2 = new ClaimedOutboxRow
        {
            RowId = Guid.NewGuid(),
            Envelope = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "B", Action = AuditAction.Created },
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var row3 = new ClaimedOutboxRow
        {
            RowId = Guid.NewGuid(),
            Envelope = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "C", Action = AuditAction.Created },
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await processor.ProcessBatchAsync([row1, row2, row3], CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(3));
        var succeeded = result.Outcomes.Where(o => o.Status == RowStatus.Succeeded).ToList();
        var failed = result.Outcomes.Where(o => o.Status == RowStatus.Failed).ToList();

        Assert.That(succeeded, Has.Count.EqualTo(1));
        Assert.That(failed, Has.Count.EqualTo(2));
        Assert.That(failed.All(f => f.ErrorMessage!.Contains("Writer did not return outcome")), Is.True);
    }

    [Test]
    public async Task AuditBatchProcessor_WriterReturnsAllOutcomes_NoExtraFailures()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());

        var eventWriter = new Mock<IAuditEventBatchWriter>();
        var processor = new AuditBatchProcessor(
            entityWriter.Object, eventWriter.Object, TimeProvider.System, NullLogger<AuditBatchProcessor>.Instance);

        var rows = Enumerable.Range(0, 5)
            .Select(i => new ClaimedOutboxRow
            {
                RowId = Guid.NewGuid(),
                Envelope = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = $"E{i}", Action = AuditAction.Created },
                AttemptCount = 0,
                CreatedAt = DateTimeOffset.UtcNow
            })
            .ToList();

        var result = await processor.ProcessBatchAsync(rows, CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(5));
        Assert.That(result.Outcomes.All(o => o.Status == RowStatus.Succeeded), Is.True);
    }

    [Test]
    public async Task AuditBatchProcessor_WriterReturnsEmpty_AllRowsFail()
    {
        var entityWriter = new Mock<IAuditEntityBatchWriter>();
        entityWriter
            .Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<WriteOutcome>());

        var eventWriter = new Mock<IAuditEventBatchWriter>();
        var processor = new AuditBatchProcessor(
            entityWriter.Object, eventWriter.Object, TimeProvider.System, NullLogger<AuditBatchProcessor>.Instance);

        var row = new ClaimedOutboxRow
        {
            RowId = Guid.NewGuid(),
            Envelope = new AuditEnvelope { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created },
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var result = await processor.ProcessBatchAsync([row], CancellationToken.None);

        Assert.That(result.Outcomes, Has.Count.EqualTo(1));
        Assert.That(result.Outcomes[0].Status, Is.EqualTo(RowStatus.Failed));
    }

    #endregion

    #region Finding 5: AuditOutboxWriter validates schema name

    [Test]
    public void AuditOutboxWriter_ValidSchemaName_DoesNotThrow()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "audit" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.DoesNotThrow(() => new AuditOutboxWriter(accessor, options, logger));
    }

    [Test]
    public void AuditOutboxWriter_SchemaWithUnderscore_DoesNotThrow()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "my_audit_schema" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.DoesNotThrow(() => new AuditOutboxWriter(accessor, options, logger));
    }

    [Test]
    public void AuditOutboxWriter_NullSchema_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = null! });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
    }

    [Test]
    public void AuditOutboxWriter_EmptySchema_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
    }

    [Test]
    public void AuditOutboxWriter_WhitespaceSchema_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "   " });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
    }

    [Test]
    public void AuditOutboxWriter_SchemaWithBrackets_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "[audit]" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        var ex = Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
        Assert.That(ex!.Message, Does.Contain("invalid character"));
    }

    [Test]
    public void AuditOutboxWriter_SchemaWithSemicolon_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "audit; DROP TABLE" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        var ex = Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
        Assert.That(ex!.Message, Does.Contain("invalid character"));
    }

    [Test]
    public void AuditOutboxWriter_SchemaWithQuote_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = "audit'" });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        var ex = Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
        Assert.That(ex!.Message, Does.Contain("invalid character"));
    }

    [Test]
    public void AuditOutboxWriter_SchemaTooLong_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = new string('a', 129) });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        var ex = Assert.Throws<ArgumentException>(() => new AuditOutboxWriter(accessor, options, logger));
        Assert.That(ex!.Message, Does.Contain("exceeds maximum length"));
    }

    [Test]
    public void AuditOutboxWriter_SchemaMaxLength_DoesNotThrow()
    {
        var accessor = new ConsumerDbContextAccessor();
        var options = Options.Create(new EntityFrameworkOptions { Schema = new string('a', 128) });
        var logger = NullLogger<AuditOutboxWriter>.Instance;

        Assert.DoesNotThrow(() => new AuditOutboxWriter(accessor, options, logger));
    }

    #endregion

    #region Helper Methods

    private static Mock<IAuditEntityBatchWriter> CreateSuccessEntityWriter()
    {
        var mock = new Mock<IAuditEntityBatchWriter>();
        mock.Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());
        return mock;
    }

    private static Mock<IAuditEventBatchWriter> CreateSuccessEventWriter()
    {
        var mock = new Mock<IAuditEventBatchWriter>();
        mock.Setup(w => w.WriteBatchAsync(It.IsAny<IReadOnlyList<AuditEnvelope>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditEnvelope> envelopes, CancellationToken _) =>
                envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());
        return mock;
    }

    private sealed class RecordingWriter : IAuditOutboxWriter
    {
        public Guid? LastIdempotencyKey { get; private set; }

        public Task<bool> WriteAsync(string envelopeJson, int envelopeVersion, Guid idempotencyKey, CancellationToken cancellationToken = default)
        {
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(true);
        }

        public Task<int> WriteBatchAsync(IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken cancellationToken = default)
        {
            if (rows.Count > 0)
                LastIdempotencyKey = rows[0].idempotencyKey;
            return Task.FromResult(rows.Count);
        }
    }

    #endregion
}
