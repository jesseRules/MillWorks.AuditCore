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
            Assert.That(deserialized!.Kind, Is.EqualTo(AuditEnvelopeKind.EntityChange));
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
        public string? LastJson { get; private set; }
        public int LastVersion { get; private set; }

        public Task WriteAsync(string envelopeJson, int envelopeVersion, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastJson = envelopeJson;
            LastVersion = envelopeVersion;
            return Task.CompletedTask;
        }
    }
}
