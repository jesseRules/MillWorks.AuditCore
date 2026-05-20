using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Sinks.Writers;

[TestFixture]
[Category("Unit")]
public sealed class AuditEntityBatchWriterTests
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private AuditEntityBatchWriter _writer = null!;

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

        _writer = new AuditEntityBatchWriter(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
        _connection.Dispose();
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
    }

    [Test]
    public async Task WriteBatchAsync_SingleEnvelope_ReturnsSuccessOutcome()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
        };

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].EnvelopeId, Is.EqualTo(envelope.EnvelopeId));
        Assert.That(outcomes[0].Succeeded, Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_MultipleEnvelopes_ReturnsOutcomePerEnvelope()
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
                EntityName = "Visit",
                Action = AuditAction.Updated,
                PropertyChanges = [new AuditEnvelopePropertyChange("Status", "A", "B")],
            },
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Appointment",
                Action = AuditAction.Deleted,
            },
        };

        var outcomes = await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(3));
        var outcomeIds = outcomes.Select(o => o.EnvelopeId).ToHashSet();
        var envelopeIds = envelopes.Select(e => e.EnvelopeId).ToHashSet();
        Assert.That(outcomeIds, Is.EquivalentTo(envelopeIds));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_PersistsRowsToDatabase()
    {
        var entityId = Guid.NewGuid();
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Updated,
                EntityId = entityId,
                CorrelationId = "corr-1",
                PropertyChanges =
                [
                    new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
                    new AuditEnvelopePropertyChange("UpdatedAt", null, "2026-05-19"),
                ],
            },
        };

        await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rows = await ctx.Set<AuditLogEntity>()
            .Where(r => r.EntityId == entityId)
            .ToListAsync();

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.All(r => r.EntityName == "Patient"), Is.True);
        Assert.That(rows.All(r => r.CorrelationId == "corr-1"), Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_OutcomesCorrelateByEnvelopeId()
    {
        var envelope1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "A",
            Action = AuditAction.Created,
        };
        var envelope2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "B",
            Action = AuditAction.Updated,
        };

        var outcomes = await _writer.WriteBatchAsync([envelope1, envelope2], CancellationToken.None);

        var outcome1 = outcomes.Single(o => o.EnvelopeId == envelope1.EnvelopeId);
        var outcome2 = outcomes.Single(o => o.EnvelopeId == envelope2.EnvelopeId);

        Assert.That(outcome1.Succeeded, Is.True);
        Assert.That(outcome2.Succeeded, Is.True);
    }

    [Test]
    public async Task WriteBatchAsync_WithPropertyChanges_WritesMultipleRowsPerEnvelope()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            EntityId = Guid.NewGuid(),
            PropertyChanges =
            [
                new AuditEnvelopePropertyChange("Field1", "a", "b"),
                new AuditEnvelopePropertyChange("Field2", "c", "d"),
                new AuditEnvelopePropertyChange("Field3", "e", "f"),
            ],
        };

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);

        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rowCount = await ctx.Set<AuditLogEntity>().CountAsync();
        Assert.That(rowCount, Is.EqualTo(3));
    }

    [Test]
    public async Task WriteBatchAsync_WithoutPropertyChanges_WritesSingleRow()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Deleted,
        };

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);

        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rowCount = await ctx.Set<AuditLogEntity>().CountAsync();
        Assert.That(rowCount, Is.EqualTo(1));
    }

    [Test]
    public async Task WriteBatchAsync_100Envelopes_AllSucceed()
    {
        var envelopes = Enumerable.Range(0, 100)
            .Select(i => new AuditEnvelope
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = $"Entity{i}",
                Action = AuditAction.Created,
            })
            .ToList();

        var outcomes = await _writer.WriteBatchAsync(envelopes, CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(100));
        Assert.That(outcomes.All(o => o.Succeeded), Is.True);

        using var scope = _provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var rowCount = await ctx.Set<AuditLogEntity>().CountAsync();
        Assert.That(rowCount, Is.EqualTo(100));
    }
}
