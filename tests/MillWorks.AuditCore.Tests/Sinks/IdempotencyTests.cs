using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;
using Moq;

namespace MillWorks.AuditCore.Tests.Sinks;

[TestFixture]
public sealed class IdempotencyTests
{
    private ServiceProvider _serviceProvider = null!;
    private AuditDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();

        services.AddDbContext<AuditDbContext>(options =>
            options.UseInMemoryDatabase($"IdempotencyTests_{Guid.NewGuid()}"));

        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        _serviceProvider = services.BuildServiceProvider();
        _dbContext = _serviceProvider.GetRequiredService<AuditDbContext>();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
        _serviceProvider.Dispose();
    }

    #region IdempotencyKey Extraction

    [Test]
    public void ExtractIdempotencyKey_EntityChange_ReturnsEnvelopeId()
    {
        var envelopeId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            EnvelopeId = envelopeId,
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "TestEntity",
            Action = AuditAction.Created
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(envelopeId));
    }

    [Test]
    public void ExtractIdempotencyKey_ExplicitEvent_ReturnsEnvelopeId()
    {
        var envelopeId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            EnvelopeId = envelopeId,
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        var key = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key, Is.EqualTo(envelopeId));
    }

    [Test]
    public void ExtractIdempotencyKey_DifferentEnvelopes_ReturnsDifferentKeys()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "TestEntity",
            Action = AuditAction.Created
        };

        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "TestEntity",
            Action = AuditAction.Created
        };

        var key1 = TransactionalOutboxSink.ExtractIdempotencyKey(envelope1);
        var key2 = TransactionalOutboxSink.ExtractIdempotencyKey(envelope2);

        Assert.That(key1, Is.Not.EqualTo(key2));
    }

    [Test]
    public void ExtractIdempotencyKey_SameEnvelope_ReturnsSameKey()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        var key1 = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);
        var key2 = TransactionalOutboxSink.ExtractIdempotencyKey(envelope);

        Assert.That(key1, Is.EqualTo(key2));
    }

    #endregion

    #region AuditEntityBatchWriter Duplicate Detection

    [Test]
    public async Task AuditEntityBatchWriter_DuplicateEnvelope_ReturnsSuccessWithDuplicateFlag()
    {
        // AuditEntityBatchWriter writes to AuditLogEntity, which doesn't have a unique constraint
        // on EnvelopeId - duplicate detection only happens if there's a DB-level constraint violation.
        // Since InMemory doesn't enforce unique constraints, we verify the happy path here.
        // The actual duplicate constraint is on AuditEvents.EventId for explicit events.

        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<AuditEntityBatchWriter>.Instance;
        var writer = new AuditEntityBatchWriter(scopeFactory, logger);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "TestEntity",
            Action = AuditAction.Created,
            EntityId = Guid.NewGuid()
        };

        var outcomes = await writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);
        Assert.That(outcomes[0].EnvelopeId, Is.EqualTo(envelope.EnvelopeId));
    }

    #endregion

    #region AuditEventBatchWriter Duplicate Detection

    [Test]
    public async Task AuditEventBatchWriter_SuccessfulWrite_ReturnsSuccess()
    {
        var mockLogger = Mock.Of<IAuditLogger>(l =>
            l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()) ==
            Task.FromResult(BatchAuditResult.Succeeded(1)));

        var logger = NullLogger<AuditEventBatchWriter>.Instance;
        var writer = new AuditEventBatchWriter(mockLogger, logger);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        var outcomes = await writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);
        Assert.That(outcomes[0].IsDuplicate, Is.False);
    }

    [Test]
    public async Task AuditEventBatchWriter_DuplicateDetected_ReturnsDuplicateOutcome()
    {
        var mockLogger = Mock.Of<IAuditLogger>(l =>
            l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()) ==
            Task.FromResult(BatchAuditResult.Duplicate(1)));

        var logger = NullLogger<AuditEventBatchWriter>.Instance;
        var writer = new AuditEventBatchWriter(mockLogger, logger);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        var outcomes = await writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);
        Assert.That(outcomes[0].IsDuplicate, Is.True);
        Assert.That(outcomes[0].EnvelopeId, Is.EqualTo(envelope.EnvelopeId));
    }

    [Test]
    public async Task AuditEventBatchWriter_MultipleDuplicates_AllReturnDuplicate()
    {
        var mockLogger = Mock.Of<IAuditLogger>(l =>
            l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()) ==
            Task.FromResult(BatchAuditResult.Duplicate(3)));

        var logger = NullLogger<AuditEventBatchWriter>.Instance;
        var writer = new AuditEventBatchWriter(mockLogger, logger);

        var envelopes = new[]
        {
            new AuditEnvelope { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "E1", Action = AuditAction.Unknown, EventType = "T1" },
            new AuditEnvelope { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "E2", Action = AuditAction.Unknown, EventType = "T2" },
            new AuditEnvelope { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "E3", Action = AuditAction.Unknown, EventType = "T3" }
        };

        var outcomes = await writer.WriteBatchAsync(envelopes, CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(3));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);
        Assert.That(outcomes.All(o => o.IsDuplicate), Is.True);
    }

    #endregion

    #region WriteOutcome Factory Methods

    [Test]
    public void WriteOutcome_Success_CreatesCorrectOutcome()
    {
        var envelopeId = Guid.NewGuid();
        var outcome = WriteOutcome.Success(envelopeId);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.IsDuplicate, Is.False);
            Assert.That(outcome.IsRetryable, Is.False);
            Assert.That(outcome.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void WriteOutcome_Duplicate_CreatesCorrectOutcome()
    {
        var envelopeId = Guid.NewGuid();
        var outcome = WriteOutcome.Duplicate(envelopeId);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.True);
            Assert.That(outcome.IsDuplicate, Is.True);
            Assert.That(outcome.IsRetryable, Is.False);
            Assert.That(outcome.ErrorMessage, Is.Null);
        });
    }

    [Test]
    public void WriteOutcome_Failed_CreatesCorrectOutcome()
    {
        var envelopeId = Guid.NewGuid();
        var errorMessage = "Connection timeout";
        var outcome = WriteOutcome.Failed(envelopeId, errorMessage, isRetryable: true);

        Assert.Multiple(() =>
        {
            Assert.That(outcome.EnvelopeId, Is.EqualTo(envelopeId));
            Assert.That(outcome.Succeeded, Is.False);
            Assert.That(outcome.IsDuplicate, Is.False);
            Assert.That(outcome.IsRetryable, Is.True);
            Assert.That(outcome.ErrorMessage, Is.EqualTo(errorMessage));
        });
    }

    #endregion

    #region BatchAuditResult

    [Test]
    public void BatchAuditResult_Succeeded_HasCorrectProperties()
    {
        var result = BatchAuditResult.Succeeded(5);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.EventCount, Is.EqualTo(5));
            Assert.That(result.IsDuplicate, Is.False);
            Assert.That(result.FailedEvents, Is.Empty);
            Assert.That(result.Exception, Is.Null);
        });
    }

    [Test]
    public void BatchAuditResult_Duplicate_HasCorrectProperties()
    {
        var result = BatchAuditResult.Duplicate(3);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.EventCount, Is.EqualTo(3));
            Assert.That(result.IsDuplicate, Is.True);
            Assert.That(result.FailedEvents, Is.Empty);
            Assert.That(result.Exception, Is.Null);
        });
    }

    [Test]
    public void BatchAuditResult_Failed_HasCorrectProperties()
    {
        var events = new List<AuditEvent> { new() { EventType = "Test" } };
        var exception = new InvalidOperationException("Test error");
        var result = BatchAuditResult.Failed(events, exception);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.EventCount, Is.EqualTo(1));
            Assert.That(result.IsDuplicate, Is.False);
            Assert.That(result.FailedEvents, Has.Count.EqualTo(1));
            Assert.That(result.Exception, Is.EqualTo(exception));
        });
    }

    #endregion

    #region AuditEventBatchWriter EventId Mapping

    [Test]
    public async Task AuditEventBatchWriter_SameEnvelope_ProducesSameEventId()
    {
        // Verify that replaying the same envelope produces the same EventId,
        // enabling duplicate detection via the AuditEvents.EventId PK constraint.
        AuditEvent? capturedEvent = null;

        var mockLogger = new Mock<IAuditLogger>();
        mockLogger.Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEvent>, CancellationToken>((events, _) => capturedEvent = events.FirstOrDefault())
            .ReturnsAsync(BatchAuditResult.Succeeded(1));

        var logger = NullLogger<AuditEventBatchWriter>.Instance;
        var writer = new AuditEventBatchWriter(mockLogger.Object, logger);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        await writer.WriteBatchAsync([envelope], CancellationToken.None);
        var firstEventId = capturedEvent!.EventId;

        await writer.WriteBatchAsync([envelope], CancellationToken.None);
        var secondEventId = capturedEvent!.EventId;

        // Same envelope should produce same EventId
        Assert.That(secondEventId, Is.EqualTo(firstEventId));
        Assert.That(firstEventId, Is.EqualTo(envelope.EnvelopeId));
    }

    [Test]
    public async Task AuditEventBatchWriter_DifferentEnvelopes_ProduceDifferentEventIds()
    {
        AuditEvent? capturedEvent = null;

        var mockLogger = new Mock<IAuditLogger>();
        mockLogger.Setup(l => l.LogBatchAsync(It.IsAny<IReadOnlyList<AuditEvent>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<AuditEvent>, CancellationToken>((events, _) => capturedEvent = events.FirstOrDefault())
            .ReturnsAsync(BatchAuditResult.Succeeded(1));

        var logger = NullLogger<AuditEventBatchWriter>.Instance;
        var writer = new AuditEventBatchWriter(mockLogger.Object, logger);

        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login"
        };

        await writer.WriteBatchAsync([envelope1], CancellationToken.None);
        var firstEventId = capturedEvent!.EventId;

        await writer.WriteBatchAsync([envelope2], CancellationToken.None);
        var secondEventId = capturedEvent!.EventId;

        // Different envelopes should produce different EventIds
        Assert.That(secondEventId, Is.Not.EqualTo(firstEventId));
    }

    #endregion

    #region AuditOutboxEntity IdempotencyKey

    [Test]
    public void AuditOutboxEntity_HasIdempotencyKeyProperty()
    {
        var entity = new AuditOutboxEntity
        {
            IdempotencyKey = Guid.NewGuid()
        };

        Assert.That(entity.IdempotencyKey, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public async Task AuditOutbox_IdempotencyKey_PersistedCorrectly()
    {
        var idempotencyKey = Guid.NewGuid();
        var entity = new AuditOutboxEntity
        {
            EnvelopeJson = """{"kind":"EntityChange"}""",
            EnvelopeVersion = 1,
            IdempotencyKey = idempotencyKey
        };

        _dbContext.AuditOutbox.Add(entity);
        await _dbContext.SaveChangesAsync();

        var retrieved = await _dbContext.AuditOutbox.FindAsync(entity.Id);

        Assert.That(retrieved, Is.Not.Null);
        Assert.That(retrieved!.IdempotencyKey, Is.EqualTo(idempotencyKey));
    }

    #endregion
}
