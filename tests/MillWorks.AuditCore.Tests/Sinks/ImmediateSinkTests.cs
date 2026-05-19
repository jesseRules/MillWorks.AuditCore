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

namespace MillWorks.AuditCore.Tests.Sinks;

[TestFixture]
[Category("Unit")]
public sealed class ImmediateSinkTests
{
    private Mock<IAuditLogger> _auditLogger = null!;
    private RecordingEntityWriter _entityWriter = null!;
    private ImmediateSink _sink = null!;

    [SetUp]
    public void SetUp()
    {
        _auditLogger = new Mock<IAuditLogger>();
        _entityWriter = new RecordingEntityWriter();
        _sink = new ImmediateSink(
            _auditLogger.Object,
            _entityWriter,
            NullLogger<ImmediateSink>.Instance);
    }

    private sealed class RecordingEntityWriter : IAuditEntityWriter
    {
        public int CallCount { get; private set; }
        public int BatchCallCount { get; private set; }
        public AuditEnvelope? LastEnvelope { get; private set; }
        public List<AuditEnvelope> AllEnvelopes { get; } = [];

        public Task WriteEntityChangeAsync(AuditEnvelope envelope, CancellationToken cancellationToken)
        {
            CallCount++;
            LastEnvelope = envelope;
            AllEnvelopes.Add(envelope);
            return Task.CompletedTask;
        }

        public Task WriteBatchAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken cancellationToken)
        {
            BatchCallCount++;
            foreach (var envelope in envelopes)
            {
                LastEnvelope = envelope;
                AllEnvelopes.Add(envelope);
            }

            return Task.CompletedTask;
        }
    }

    [Test]
    public void PublishAsync_NullEnvelope_Throws()
    {
        Assert.ThrowsAsync<ArgumentNullException>(() => _sink.PublishAsync(null!, CancellationToken.None));
    }

    [Test]
    public async Task PublishAsync_EntityChange_DelegatesToEntityWriter()
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

        Assert.That(_entityWriter.CallCount, Is.EqualTo(1));
        Assert.That(_entityWriter.LastEnvelope, Is.SameAs(envelope));
        _auditLogger.VerifyNoOtherCalls();
    }

    [Test]
    public async Task PublishAsync_ExplicitEvent_DelegatesToAuditLogger()
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

        AuditEvent? captured = null;
        _auditLogger
            .Setup(static l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sink.PublishAsync(envelope);

        _auditLogger.Verify(static l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(_entityWriter.CallCount, Is.Zero);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.EventType, Is.EqualTo("User.Login"));
        Assert.That(captured.EntityName, Is.EqualTo("User.Login"));
        Assert.That(captured.Action, Is.EqualTo(AuditAction.Unknown));
        Assert.That(captured.StartDate, Is.EqualTo(occurredAt));
        Assert.That(captured.AspNetUserId, Is.EqualTo("alice"));
        Assert.That(captured.CorrelationId, Is.EqualTo("corr-1"));
        Assert.That(captured.IpAddress, Is.EqualTo("10.0.0.1"));
        Assert.That(captured.UserAgent, Is.EqualTo("ua/1"));
        Assert.That(captured.KeyValues["Id"], Is.EqualTo(envelope.EntityId));
        Assert.That(captured.CustomFields["Description"], Is.EqualTo("Login OK"));
        Assert.That(captured.CustomFields["AdditionalData"], Is.EqualTo("{\"method\":\"oauth\"}"));
    }

    [Test]
    public async Task PublishAsync_ExplicitEvent_OmitsOptionalFieldsWhenNull()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EntityName = "User.Logout",
            Action = AuditAction.Unknown,
            EventType = "User.Logout",
        };

        AuditEvent? captured = null;
        _auditLogger
            .Setup(static l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => captured = e)
            .Returns(Task.CompletedTask);

        await _sink.PublishAsync(envelope);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.KeyValues, Is.Empty);
        Assert.That(captured.CustomFields, Is.Empty);
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
    public void DiResolution_IAuditSink_ReturnsImmediateSink()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Mock.Of<IAuditLogger>());
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(connection));
        services.AddScoped<IAuditEntityWriter, AuditDbContextEntityWriter>();
        services.AddScoped<IAuditSink, ImmediateSink>();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var sink = scope.ServiceProvider.GetRequiredService<IAuditSink>();

        Assert.That(sink, Is.TypeOf<ImmediateSink>());
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

        Assert.That(_entityWriter.BatchCallCount, Is.Zero);
        _auditLogger.VerifyNoOtherCalls();
    }

    [Test]
    public async Task PublishBatchAsync_MultipleEntityChanges_DelegatesToWriterInSingleBatch()
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

        Assert.That(_entityWriter.BatchCallCount, Is.EqualTo(1), "Should call WriteBatchAsync once");
        Assert.That(_entityWriter.AllEnvelopes, Has.Count.EqualTo(3));
        _auditLogger.VerifyNoOtherCalls();
    }

    [Test]
    public async Task PublishBatchAsync_MixedKinds_BatchesEntityChanges_IndividualExplicitEvents()
    {
        AuditEvent? capturedEvent = null;
        _auditLogger
            .Setup(static l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((e, _) => capturedEvent = e)
            .Returns(Task.CompletedTask);

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
        };

        await _sink.PublishBatchAsync(envelopes, CancellationToken.None);

        Assert.That(_entityWriter.BatchCallCount, Is.EqualTo(1), "EntityChanges batched");
        Assert.That(_entityWriter.AllEnvelopes, Has.Count.EqualTo(2));
        _auditLogger.Verify(static l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "ExplicitEvent forwarded individually");
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.EventType, Is.EqualTo("User.Login"));
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

        // Expected: 1 row for Created (no property changes), 2 rows for Updated, 1 row for Deleted
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
