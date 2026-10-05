using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
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
    private RaceSaveInterceptor _saveInterceptor = null!;
    private SqliteConnection _connection = null!;
    private ServiceProvider _provider = null!;
    private AuditEntityBatchWriter _writer = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _saveInterceptor = new RaceSaveInterceptor();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection).AddInterceptors(_saveInterceptor));
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
        var tenantId = Guid.NewGuid();
        var subjectId = Guid.NewGuid();
        var envelopes = new List<AuditEnvelope>
        {
            new()
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "Patient",
                Action = AuditAction.Updated,
                EntityId = entityId,
                CorrelationId = "corr-1",
                GovernanceIdentity = new AuditGovernanceIdentity(tenantId, "test:patient", entityId),
                SubjectReferences = [new AuditSubjectReference(subjectId, "PatientId")],
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
        Assert.That(rows.All(r => r.GovernanceMetadata is not null), Is.True);
        Assert.That(
            rows.Select(r => JsonSerializer.Deserialize<AuditGovernanceIdentity>(r.GovernanceMetadata!)),
            Is.All.EqualTo(new AuditGovernanceIdentity(tenantId, "test:patient", entityId)));
        var links = await ctx.AuditLogSubjectLinks
            .Where(link => link.SubjectId == subjectId)
            .ToListAsync();
        Assert.That(links, Has.Count.EqualTo(2));
        Assert.That(links.Select(static link => link.AuditLogId), Is.EquivalentTo(rows.Select(static row => row.Id)));
        Assert.That(links, Has.All.Matches<AuditLogSubjectLinkEntity>(link =>
            link.TenantId == tenantId && link.Relationship == "PatientId"));
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

    [Test]
    public async Task WriteBatchAsync_DbUpdateException_ReturnsFailedOutcomes()
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            AdditionalData = new string('x', 1_000_000),
        };

        _connection.Dispose();
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AuditDbContext>(o => o.UseSqlite(_connection));
        var localProvider = services.BuildServiceProvider();

        using (var scope = localProvider.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            ctx.Database.EnsureCreated();
        }

        var writer = new AuditEntityBatchWriter(
            localProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance);

        var outcomes = await writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes, Has.Count.EqualTo(1));
        Assert.That(outcomes[0].Succeeded, Is.True);

        localProvider.Dispose();
    }
    [TestCase(false)]
    [TestCase(true)]
    public async Task WriteBatchAsync_MixedReplay_PersistsNewEnvelope(bool withoutProperties)
    {
        var existing = ReplayEnvelope(withoutProperties: withoutProperties);
        var fresh = ReplayEnvelope(withoutProperties: withoutProperties);
        await _writer.WriteBatchAsync([existing], CancellationToken.None);

        var outcomes = await _writer.WriteBatchAsync([existing, fresh], CancellationToken.None);

        Assert.That(outcomes[0].IsDuplicate, Is.True);
        Assert.That(outcomes[1].Succeeded, Is.True);
        Assert.That(outcomes[1].IsDuplicate, Is.False);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await db.AuditLogs.CountAsync(), Is.EqualTo(withoutProperties ? 2 : 4));
        Assert.That(await db.AuditLogSubjectLinks.CountAsync(), Is.EqualTo(withoutProperties ? 2 : 4));
    }

    [Test]
    public async Task WriteBatchAsync_PartialReplay_PersistsMissingPropertyAndSubjectLink()
    {
        var envelope = ReplayEnvelope();
        var partial = ReplayEnvelope(envelope.EnvelopeId, firstPropertyOnly: true);
        await _writer.WriteBatchAsync([partial], CancellationToken.None);

        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);
        Assert.That(outcomes[0].Succeeded, Is.True);
        Assert.That(outcomes[0].IsDuplicate, Is.False);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await db.AuditLogs.Select(row => row.PropertyName).ToListAsync(),
            Is.EquivalentTo(new[] { "Status", "Name" }));
        Assert.That(await db.AuditLogSubjectLinks.CountAsync(), Is.EqualTo(2));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WriteBatchAsync_RepeatedEnvelopeInBatch_WritesEachRowOnce(bool withoutProperties)
    {
        var envelope = ReplayEnvelope(withoutProperties: withoutProperties);
        var outcomes = await _writer.WriteBatchAsync([envelope, envelope], CancellationToken.None);
        Assert.That(outcomes, Has.Count.EqualTo(2));
        Assert.That(outcomes.All(static outcome => outcome.Succeeded), Is.True);
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await db.AuditLogs.CountAsync(), Is.EqualTo(withoutProperties ? 1 : 2));
        Assert.That(await db.AuditLogSubjectLinks.CountAsync(), Is.EqualTo(withoutProperties ? 1 : 2));
    }

    [Test]
    public async Task WriteBatchAsync_CompleteReplay_DoesNotAttemptAnotherSave()
    {
        var envelope = ReplayEnvelope();
        await _writer.WriteBatchAsync([envelope], CancellationToken.None);
        var saves = _saveInterceptor.SaveCount;
        var outcomes = await _writer.WriteBatchAsync([envelope], CancellationToken.None);
        Assert.That(outcomes[0].IsDuplicate, Is.True);
        Assert.That(_saveInterceptor.SaveCount, Is.EqualTo(saves));
    }

    [Test]
    public async Task WriteBatchAsync_ConcurrentPartialReplay_RechecksAndPersistsRemainingBatch()
    {
        var envelope = ReplayEnvelope();
        var fresh = ReplayEnvelope();
        var partial = ReplayEnvelope(envelope.EnvelopeId, firstPropertyOnly: true);
        // Commit a competing row after the writer's lookup but before its INSERT.
        _saveInterceptor.BeforeSave = async () =>
            await _writer.WriteBatchAsync([partial], CancellationToken.None);

        var outcomes = await _writer.WriteBatchAsync([envelope, fresh], CancellationToken.None);

        Assert.That(outcomes.All(static outcome => outcome.Succeeded && !outcome.IsDuplicate), Is.True);
        Assert.That(_saveInterceptor.SaveCount, Is.EqualTo(3));
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        Assert.That(await db.AuditLogs.CountAsync(), Is.EqualTo(4));
        Assert.That(await db.AuditLogSubjectLinks.CountAsync(), Is.EqualTo(4));
    }

    [Test]
    public async Task WriteBatchAsync_UnrelatedUniqueViolation_DoesNotAcknowledgeUnwrittenEnvelopes()
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX TestEntityName ON AuditLogs(EntityType)");
        await _writer.WriteBatchAsync([ReplayEnvelope(withoutProperties: true)], CancellationToken.None);
        var outcomes = await _writer.WriteBatchAsync([ReplayEnvelope(withoutProperties: true)], CancellationToken.None);

        Assert.That(outcomes[0].Succeeded, Is.False);
        Assert.That(outcomes[0].IsDuplicate, Is.False);
        Assert.That(outcomes[0].IsRetryable, Is.True);
        Assert.That(_saveInterceptor.SaveCount, Is.EqualTo(4));
        Assert.That(await db.AuditLogs.CountAsync(), Is.EqualTo(1));
    }

    private static AuditEnvelope ReplayEnvelope(
        Guid? envelopeId = null, bool withoutProperties = false, bool firstPropertyOnly = false) => new()
    {
        EnvelopeId = envelopeId ?? Guid.NewGuid(),
        Kind = AuditEnvelopeKind.EntityChange,
        EntityName = "Patient",
        Action = AuditAction.Updated,
        GovernanceIdentity = new AuditGovernanceIdentity(
            Guid.Parse("11111111-1111-1111-1111-111111111111"), "test:patient", Guid.Parse("33333333-3333-3333-3333-333333333333")),
        SubjectReferences = [new AuditSubjectReference(Guid.Parse("22222222-2222-2222-2222-222222222222"), "PatientId")],
        PropertyChanges = withoutProperties ? null : firstPropertyOnly
            ? [new AuditEnvelopePropertyChange("Status", "Pending", "Active")]
            : [new AuditEnvelopePropertyChange("Status", "Pending", "Active"),
               new AuditEnvelopePropertyChange("Name", "Old", "New")]
    };

    private sealed class RaceSaveInterceptor : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public Func<Task>? BeforeSave { get; set; }
        public int SaveCount { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            SaveCount++;
            var callback = BeforeSave;
            BeforeSave = null;
            if (callback is not null)
                await callback();
            return result;
        }
    }
}
