using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Sinks;

namespace MillWorks.AuditCore.Tests.Sinks;

/// <summary>
/// Unit tests for <see cref="TransactionalOutboxSink"/> and
/// <see cref="ConsumerDbContextAccessor"/>. Tests envelope serialization and
/// accessor behavior. Full SQL Server integration tests for the raw-SQL writer
/// are in <c>OutboxDrainerIntegrationTests</c>.
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class TransactionalOutboxSinkTests
{
    [Test]
    public async Task PublishAsync_SerializesEnvelopeToJson()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            UserId = "user-123",
            CorrelationId = "corr-abc",
        };

        await sink.PublishAsync(envelope);

        Assert.That(writer.CallCount, Is.EqualTo(1));
        Assert.That(writer.LastVersion, Is.EqualTo(TransactionalOutboxSink.CurrentEnvelopeVersion));

        var deserialized = JsonSerializer.Deserialize<AuditEnvelope>(
            writer.LastJson!,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.That(deserialized, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(deserialized!.EnvelopeId, Is.EqualTo(envelope.EnvelopeId),
                "EnvelopeId must survive serialization round-trip");
            Assert.That(deserialized.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
            Assert.That(deserialized.EntityName, Is.EqualTo("Patient"));
            Assert.That(deserialized.Action, Is.EqualTo(AuditAction.Created));
            Assert.That(deserialized.UserId, Is.EqualTo("user-123"));
            Assert.That(deserialized.CorrelationId, Is.EqualTo("corr-abc"));
        });
    }

    [Test]
    public void PublishAsync_NullEnvelope_Throws()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await sink.PublishAsync(null!));
    }

    [Test]
    public void PublishBatchAsync_NullEnvelopes_Throws()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await sink.PublishBatchAsync(null!));
    }

    [Test]
    public async Task PublishBatchAsync_EmptyList_NoOps()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        await sink.PublishBatchAsync([]);

        Assert.That(writer.BatchCallCount, Is.Zero);
        Assert.That(writer.AllRows, Is.Empty);
    }

    [Test]
    public async Task PublishBatchAsync_MultipleEnvelopes_WritesAllInSingleBatch()
    {
        var writer = new RecordingWriter();
        var sink = new TransactionalOutboxSink(writer, NullLogger<TransactionalOutboxSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Created,
                CorrelationId = "batch-1",
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Visit",
                Action = AuditAction.Updated,
                CorrelationId = "batch-2",
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.Login",
                Action = AuditAction.Unknown,
                EventType = "User.Login",
                CorrelationId = "batch-3",
            },
        };

        await sink.PublishBatchAsync(envelopes);

        Assert.That(writer.BatchCallCount, Is.EqualTo(1), "Should call WriteBatchAsync once");
        Assert.That(writer.AllRows, Has.Count.EqualTo(3));
        Assert.That(writer.AllRows.All(r => r.version == TransactionalOutboxSink.CurrentEnvelopeVersion));

        // Verify each envelope was serialized correctly
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var deserialized = writer.AllRows
            .Select(r => JsonSerializer.Deserialize<AuditEnvelope>(r.json, options)!)
            .ToList();

        Assert.That(deserialized[0].EntityName, Is.EqualTo("Patient"));
        Assert.That(deserialized[0].CorrelationId, Is.EqualTo("batch-1"));
        Assert.That(deserialized[1].EntityName, Is.EqualTo("Visit"));
        Assert.That(deserialized[1].CorrelationId, Is.EqualTo("batch-2"));
        Assert.That(deserialized[2].EventType, Is.EqualTo("User.Login"));
        Assert.That(deserialized[2].CorrelationId, Is.EqualTo("batch-3"));
    }

    [Test]
    public void Accessor_Current_ThrowsWhenNotSet()
    {
        var accessor = new ConsumerDbContextAccessor();

        var ex = Assert.Throws<InvalidOperationException>(() => _ = accessor.Current);
        Assert.That(ex!.Message, Does.Contain("No consumer DbContext"));
    }

    [Test]
    public void Accessor_SetCurrent_NestedCall_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();
        var mockCtx = Mock.Of<Microsoft.EntityFrameworkCore.DbContext>();

        using (accessor.SetCurrent(mockCtx))
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => accessor.SetCurrent(mockCtx));

            Assert.That(ex!.Message, Does.Contain("already set"));
        }
    }

    [Test]
    public void Accessor_HasCurrent_ReflectsState()
    {
        var accessor = new ConsumerDbContextAccessor();
        var mockCtx = Mock.Of<Microsoft.EntityFrameworkCore.DbContext>();

        Assert.That(accessor.HasCurrent, Is.False);

        using (accessor.SetCurrent(mockCtx))
        {
            Assert.That(accessor.HasCurrent, Is.True);
            Assert.That(accessor.Current, Is.SameAs(mockCtx));
        }

        Assert.That(accessor.HasCurrent, Is.False);
    }

    [Test]
    public void Accessor_SetCurrent_NullContext_Throws()
    {
        var accessor = new ConsumerDbContextAccessor();

        Assert.Throws<ArgumentNullException>(() => accessor.SetCurrent(null!));
    }

    [Test]
    public void Accessor_Dispose_ClearsContext()
    {
        var accessor = new ConsumerDbContextAccessor();
        var mockCtx = Mock.Of<Microsoft.EntityFrameworkCore.DbContext>();

        var scope = accessor.SetCurrent(mockCtx);
        Assert.That(accessor.HasCurrent, Is.True);

        scope.Dispose();
        Assert.That(accessor.HasCurrent, Is.False);

        // Double dispose is safe
        scope.Dispose();
        Assert.That(accessor.HasCurrent, Is.False);
    }

    private sealed class RecordingWriter : IAuditOutboxWriter
    {
        public int CallCount { get; private set; }
        public int BatchCallCount { get; private set; }
        public string? LastJson { get; private set; }
        public int LastVersion { get; private set; }
        public Guid? LastIdempotencyKey { get; private set; }
        public List<(string json, int version, Guid idempotencyKey)> AllRows { get; } = [];

        public Task<bool> WriteAsync(string envelopeJson, int envelopeVersion, Guid idempotencyKey, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastJson = envelopeJson;
            LastVersion = envelopeVersion;
            LastIdempotencyKey = idempotencyKey;
            AllRows.Add((envelopeJson, envelopeVersion, idempotencyKey));
            return Task.FromResult(true);
        }

        public Task<int> WriteBatchAsync(IReadOnlyList<(string envelopeJson, int envelopeVersion, Guid idempotencyKey)> rows, CancellationToken cancellationToken = default)
        {
            BatchCallCount++;
            foreach (var row in rows)
            {
                LastJson = row.envelopeJson;
                LastVersion = row.envelopeVersion;
                LastIdempotencyKey = row.idempotencyKey;
                AllRows.Add(row);
            }
            return Task.FromResult(rows.Count);
        }
    }
}
