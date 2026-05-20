using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Sinks;

[TestFixture]
[Category("Unit")]
public sealed class ImmediateSinkTests
{
    private RecordingEntityBatchWriter _entityBatchWriter = null!;
    private RecordingEventBatchWriter _eventBatchWriter = null!;
    private ImmediateSink _sink = null!;

    [SetUp]
    public void SetUp()
    {
        _entityBatchWriter = new RecordingEntityBatchWriter();
        _eventBatchWriter = new RecordingEventBatchWriter();
        _sink = new ImmediateSink(
            _entityBatchWriter,
            _eventBatchWriter,
            NullLogger<ImmediateSink>.Instance);
    }

    private sealed class RecordingEntityBatchWriter : IAuditEntityBatchWriter
    {
        public int BatchCallCount { get; private set; }
        public List<AuditEnvelope> AllEnvelopes { get; } = [];

        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            var outcomes = new List<WriteOutcome>();
            foreach (var envelope in envelopes)
            {
                AllEnvelopes.Add(envelope);
                outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
            }

            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    private sealed class RecordingEventBatchWriter : IAuditEventBatchWriter
    {
        public int BatchCallCount { get; private set; }
        public List<AuditEnvelope> AllEnvelopes { get; } = [];

        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken)
        {
            BatchCallCount++;
            var outcomes = new List<WriteOutcome>();
            foreach (var envelope in envelopes)
            {
                AllEnvelopes.Add(envelope);
                outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
            }

            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    [Test]
    public void PublishAsync_NullEnvelope_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _sink.PublishAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task PublishAsync_EntityChange_DelegatesToEntityBatchWriter()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            PropertyChanges =
            [
                new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
            ],
        };

        await _sink.PublishAsync(envelope);

        Assert.That(_entityBatchWriter.BatchCallCount, Is.EqualTo(1));
        Assert.That(_entityBatchWriter.AllEnvelopes, Has.Count.EqualTo(1));
        Assert.That(_entityBatchWriter.AllEnvelopes[0], Is.SameAs(envelope));
        Assert.That(_eventBatchWriter.BatchCallCount, Is.Zero);
    }

    [Test]
    public async Task PublishAsync_ExplicitEvent_DelegatesToEventBatchWriter()
    {
        var occurredAt = new DateTimeOffset(2026, 4, 26, 12, 0, 0, TimeSpan.Zero);
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Login",
            Action = AuditAction.Unknown,
            EventType = "User.Login",
            OccurredAt = occurredAt,
            UserId = "alice",
            CorrelationId = "corr-1",
            IpAddress = "10.0.0.1",
            UserAgent = "ua/1",
            Description = "Login OK",
            AdditionalData = "{\"method\":\"oauth\"}",
            EntityId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        };

        await _sink.PublishAsync(envelope);

        Assert.That(_eventBatchWriter.BatchCallCount, Is.EqualTo(1));
        Assert.That(_eventBatchWriter.AllEnvelopes, Has.Count.EqualTo(1));
        Assert.That(_eventBatchWriter.AllEnvelopes[0], Is.SameAs(envelope));
        Assert.That(_entityBatchWriter.BatchCallCount, Is.Zero);
    }

    [Test]
    public void PublishAsync_UnknownKind_Throws()
    {
        var envelope = new AuditEnvelope
        {
            Kind = (AuditEnvelopeKind)999,
            EntityName = "X",
            Action = AuditAction.Created,
        };

        Assert.ThrowsAsync<InvalidOperationException>(() => _sink.PublishAsync(envelope));
    }

    [Test]
    public void PublishBatchAsync_NullEnvelopes_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _sink.PublishBatchAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task PublishBatchAsync_EmptyList_NoOps()
    {
        await _sink.PublishBatchAsync([], CancellationToken.None);

        Assert.That(_entityBatchWriter.BatchCallCount, Is.Zero);
        Assert.That(_eventBatchWriter.BatchCallCount, Is.Zero);
    }

    [Test]
    public async Task PublishBatchAsync_MultipleEntityChanges_DelegatesInSingleBatch()
    {
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Created,
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Updated,
                PropertyChanges = [new AuditEnvelopePropertyChange("Status", "Pending", "Active")],
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Visit",
                Action = AuditAction.Deleted,
            },
        };

        await _sink.PublishBatchAsync(envelopes, CancellationToken.None);

        Assert.That(_entityBatchWriter.BatchCallCount, Is.EqualTo(1), "Should call WriteBatchAsync once");
        Assert.That(_entityBatchWriter.AllEnvelopes, Has.Count.EqualTo(3));
        Assert.That(_eventBatchWriter.BatchCallCount, Is.Zero);
    }

    [Test]
    public async Task PublishBatchAsync_MultipleExplicitEvents_DelegatesInSingleBatch()
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
        };

        await _sink.PublishBatchAsync(envelopes, CancellationToken.None);

        Assert.That(_eventBatchWriter.BatchCallCount, Is.EqualTo(1), "Should call WriteBatchAsync once");
        Assert.That(_eventBatchWriter.AllEnvelopes, Has.Count.EqualTo(2));
        Assert.That(_entityBatchWriter.BatchCallCount, Is.Zero);
    }

    [Test]
    public async Task PublishBatchAsync_MixedKinds_RoutesToCorrectWriters()
    {
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Created,
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.Login",
                Action = AuditAction.Unknown,
                EventType = "User.Login",
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Visit",
                Action = AuditAction.Updated,
            },
            new()
            {
                Kind = AuditEnvelopeKind.ExplicitEvent,
                EntityName = "User.Logout",
                Action = AuditAction.Unknown,
                EventType = "User.Logout",
            },
        };

        await _sink.PublishBatchAsync(envelopes, CancellationToken.None);

        Assert.That(_entityBatchWriter.BatchCallCount, Is.EqualTo(1), "Entity changes batched once");
        Assert.That(_entityBatchWriter.AllEnvelopes, Has.Count.EqualTo(2));
        Assert.That(_eventBatchWriter.BatchCallCount, Is.EqualTo(1), "Explicit events batched once");
        Assert.That(_eventBatchWriter.AllEnvelopes, Has.Count.EqualTo(2));
    }

    [Test]
    public void PublishBatchAsync_UnknownKind_Throws()
    {
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Created,
            },
            new()
            {
                Kind = (AuditEnvelopeKind)999,
                EntityName = "X",
                Action = AuditAction.Created,
            },
        };

        Assert.ThrowsAsync<InvalidOperationException>(() => _sink.PublishBatchAsync(envelopes, CancellationToken.None));
    }

    [Test]
    public async Task PublishBatchAsync_NoInlineSideEffects_AllEnvelopesClassifiedBeforeAnyWrite()
    {
        var writeOrder = new List<string>();

        var entityWriter = new OrderTrackingEntityBatchWriter(writeOrder);
        var eventWriter = new OrderTrackingEventBatchWriter(writeOrder);
        var sink = new ImmediateSink(entityWriter, eventWriter, NullLogger<ImmediateSink>.Instance);

        var envelopes = new List<AuditEnvelope>
        {
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "A", Action = AuditAction.Created },
            new() { Kind = AuditEnvelopeKind.ExplicitEvent, EntityName = "B", Action = AuditAction.Unknown, EventType = "B" },
            new() { Kind = AuditEnvelopeKind.EntityChange, EntityName = "C", Action = AuditAction.Updated },
        };

        await sink.PublishBatchAsync(envelopes, CancellationToken.None);

        Assert.That(writeOrder, Is.EqualTo(new[] { "entity:A,C", "event:B" }));
    }

    private sealed class OrderTrackingEntityBatchWriter(List<string> order) : IAuditEntityBatchWriter
    {
        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken)
        {
            order.Add($"entity:{string.Join(",", envelopes.Select(e => e.EntityName))}");
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(
                envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());
        }
    }

    private sealed class OrderTrackingEventBatchWriter(List<string> order) : IAuditEventBatchWriter
    {
        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken)
        {
            order.Add($"event:{string.Join(",", envelopes.Select(e => e.EntityName))}");
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(
                envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList());
        }
    }
}

[TestFixture]
[Category("Unit")]
public sealed class AuditDbContextEntityWriterTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private AuditDbContextEntityWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection));
        _provider = services.BuildServiceProvider();

        using (var scope = _provider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            ctx.Database.EnsureCreated();
        }

        _writer = new AuditDbContextEntityWriter(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditDbContextEntityWriter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task WriteEntityChangeAsync_PropertyChanges_WritesOneRowPerChange()
    {
        var entityId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            EntityId = entityId,
            CorrelationId = "corr-xyz",
            IpAddress = "10.0.0.2",
            UserAgent = "ua/test",
            Description = "Updated patient record",
            PropertyChanges =
            [
                new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
                new AuditEnvelopePropertyChange("UpdatedAt", null, "2026-04-26"),
            ],
        };

        await _writer.WriteEntityChangeAsync(envelope, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.Set<AuditLogEntity>()
            .OrderBy(static r => r.PropertyName)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.All(static r => r.EntityName == "Patient"));
        Assert.That(rows.All(r => r.EntityId == entityId));
        Assert.That(rows.All(static r => r.Action == AuditAction.Updated));
        Assert.That(rows.All(static r => r.CorrelationId == "corr-xyz"));
        Assert.That(rows.All(static r => r.IpAddress == "10.0.0.2"));
        Assert.That(rows.All(static r => r.UserAgent == "ua/test"));
        Assert.That(rows.All(static r => r.Description == "Updated patient record"));

        var status = rows.Single(static r => r.PropertyName == "Status");
        Assert.That(status.OldValue, Is.EqualTo("Pending"));
        Assert.That(status.NewValue, Is.EqualTo("Active"));

        var updatedAt = rows.Single(static r => r.PropertyName == "UpdatedAt");
        Assert.That(updatedAt.OldValue, Is.Null);
        Assert.That(updatedAt.NewValue, Is.EqualTo("2026-04-26"));
    }

    [Test]
    public async Task WriteEntityChangeAsync_PropertyChanges_PropagatesAdditionalData()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            AdditionalData = "{\"ferpa\":true}",
            PropertyChanges =
            [
                new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
            ],
        };

        await _writer.WriteEntityChangeAsync(envelope, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var row = await ctx.Set<AuditLogEntity>().SingleAsync();

        Assert.That(row.AdditionalData, Is.EqualTo("{\"ferpa\":true}"));
    }

    [Test]
    public async Task WriteEntityChangeAsync_NoPropertyChanges_WritesSingleRowWithAdditionalData()
    {
        var entityId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            EntityId = entityId,
            AdditionalData = "{\"snapshot\":\"data\"}",
            Description = "Created patient",
        };

        await _writer.WriteEntityChangeAsync(envelope, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.Set<AuditLogEntity>().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        var row = rows.Single();
        Assert.That(row.EntityName, Is.EqualTo("Patient"));
        Assert.That(row.EntityId, Is.EqualTo(entityId));
        Assert.That(row.Action, Is.EqualTo(AuditAction.Created));
        Assert.That(row.PropertyName, Is.Null);
        Assert.That(row.AdditionalData, Is.EqualTo("{\"snapshot\":\"data\"}"));
        Assert.That(row.Description, Is.EqualTo("Created patient"));
    }

    [Test]
    public async Task WriteEntityChangeAsync_EmptyPropertyChanges_WritesSingleRow()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Deleted,
            PropertyChanges = Array.Empty<AuditEnvelopePropertyChange>(),
        };

        await _writer.WriteEntityChangeAsync(envelope, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.Set<AuditLogEntity>().ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].PropertyName, Is.Null);
    }

    [Test]
    public void WriteEntityChangeAsync_NullEnvelope_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _writer.WriteEntityChangeAsync(null!, CancellationToken.None));
    }

    [Test]
    public void WriteBatchAsync_NullEnvelopes_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _writer.WriteBatchAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task WriteBatchAsync_EmptyList_NoOps()
    {
        await _writer.WriteBatchAsync([], CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await ctx.Set<AuditLogEntity>().CountAsync(), Is.Zero);
    }

    [Test]
    public async Task WriteBatchAsync_MultipleEnvelopes_WritesAllInSingleTransaction()
    {
        var entityId1 = Guid.NewGuid();
        var entityId2 = Guid.NewGuid();
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Created,
                EntityId = entityId1,
                CorrelationId = "batch-corr",
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Updated,
                EntityId = entityId2,
                CorrelationId = "batch-corr",
                PropertyChanges =
                [
                    new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
                    new AuditEnvelopePropertyChange("UpdatedAt", null, "2026-05-19"),
                ],
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Visit",
                Action = AuditAction.Deleted,
                EntityId = Guid.NewGuid(),
                CorrelationId = "batch-corr",
            },
        };

        await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.Set<AuditLogEntity>()
            .OrderBy(static r => r.EntityName)
            .ThenBy(static r => r.Action)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(4));
        Assert.That(rows.All(static r => r.CorrelationId == "batch-corr"));

        var patientCreated = rows.Single(static r => r.EntityName == "Patient" && r.Action == AuditAction.Created);
        Assert.That(patientCreated.EntityId, Is.EqualTo(entityId1));
        Assert.That(patientCreated.PropertyName, Is.Null);

        var patientUpdates = rows.Where(static r => r.EntityName == "Patient" && r.Action == AuditAction.Updated)
            .ToList();
        Assert.That(patientUpdates, Has.Count.EqualTo(2));
        Assert.That(patientUpdates.All(r => r.EntityId == entityId2));

        var visitDeleted = rows.Single(static r => r.EntityName == "Visit");
        Assert.That(visitDeleted.Action, Is.EqualTo(AuditAction.Deleted));
    }

    [Test]
    public async Task WriteBatchAsync_100Envelopes_SingleDatabaseRoundTrip()
    {
        var envelopes = Enumerable.Range(0, 100)
            .Select(static i => new AuditEnvelope
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Updated,
                EntityId = Guid.NewGuid(),
                CorrelationId = "bulk-test",
                PropertyChanges =
                [
                    new AuditEnvelopePropertyChange($"Field{i}", "old", "new"),
                ],
            })
            .ToList();

        await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        using var verifyScope = _provider.CreateScope();
        var ctx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var count = await ctx.Set<AuditLogEntity>().CountAsync();

        Assert.That(count, Is.EqualTo(100));
    }
}
