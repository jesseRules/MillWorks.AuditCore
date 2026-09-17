using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Sinks;

/// <summary>
/// SQLite-backed integration tests that verify duplicate detection at the database level.
/// InMemory provider doesn't enforce unique constraints, so these tests prove the
/// idempotency logic actually works with real constraint enforcement.
/// </summary>
[TestFixture]
public sealed class IdempotencySqliteTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AuditDbContext> _options = null!;
    private ServiceProvider _serviceProvider = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        var services = new ServiceCollection();
        services.AddDbContext<AuditDbContext>(opts =>
            opts.UseSqlite(_connection)
                .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning)));
        services.AddLogging();

        _serviceProvider = services.BuildServiceProvider();

        using var context = new AuditDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _serviceProvider.Dispose();
        _connection.Close();
        _connection.Dispose();
    }

    public void Dispose()
    {
        _serviceProvider?.Dispose();
        _connection?.Dispose();
    }

    #region AuditOutbox IdempotencyKey Constraint

    [Test]
    public async Task AuditOutbox_DuplicateIdempotencyKey_ThrowsConstraintViolation()
    {
        var idempotencyKey = Guid.NewGuid();

        await using var context = new AuditDbContext(_options);

        var row1 = new AuditOutboxEntity
        {
            EnvelopeJson = """{"kind":"EntityChange"}""",
            EnvelopeVersion = 1,
            IdempotencyKey = idempotencyKey
        };

        var row2 = new AuditOutboxEntity
        {
            EnvelopeJson = """{"kind":"EntityChange"}""",
            EnvelopeVersion = 1,
            IdempotencyKey = idempotencyKey // Same key - should violate constraint
        };

        context.AuditOutbox.Add(row1);
        await context.SaveChangesAsync();

        context.AuditOutbox.Add(row2);

        var ex = Assert.ThrowsAsync<DbUpdateException>(async () =>
            await context.SaveChangesAsync());

        Assert.That(ex!.InnerException?.Message, Does.Contain("UNIQUE constraint"));
    }

    [Test]
    public async Task AuditOutbox_DifferentIdempotencyKeys_BothSucceed()
    {
        await using var context = new AuditDbContext(_options);

        var row1 = new AuditOutboxEntity
        {
            EnvelopeJson = """{"kind":"EntityChange"}""",
            EnvelopeVersion = 1,
            IdempotencyKey = Guid.NewGuid()
        };

        var row2 = new AuditOutboxEntity
        {
            EnvelopeJson = """{"kind":"EntityChange"}""",
            EnvelopeVersion = 1,
            IdempotencyKey = Guid.NewGuid()
        };

        context.AuditOutbox.AddRange(row1, row2);
        var saved = await context.SaveChangesAsync();

        Assert.That(saved, Is.EqualTo(2));
    }

    #endregion

    #region AuditEvent EventId Constraint (PK)

    [Test]
    public async Task AuditEvent_DuplicateEventId_ThrowsConstraintViolation()
    {
        var eventId = Guid.NewGuid();

        // Insert first event
        await using (var context1 = new AuditDbContext(_options))
        {
            var event1 = new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Test.Event"
            };

            context1.AuditEvents.Add(event1);
            await context1.SaveChangesAsync();
        }

        // Try to insert duplicate in a fresh context (simulates different request)
        await using var context2 = new AuditDbContext(_options);
        var event2 = new AuditEventEntity
        {
            EventId = eventId, // Same EventId - violates PK
            EventType = "Test.Event"
        };

        context2.AuditEvents.Add(event2);

        var ex = Assert.ThrowsAsync<DbUpdateException>(async () =>
            await context2.SaveChangesAsync());

        // SQLite reports PK violation as UNIQUE constraint
        Assert.That(ex!.InnerException?.Message, Does.Contain("UNIQUE constraint"));
    }

    #endregion

    #region AuditEntityBatchWriter with SQLite

    [Test]
    public async Task AuditEntityBatchWriter_WriteSameEnvelopeTwice_SecondIsDuplicate()
    {
        var scopeFactory = _serviceProvider.GetRequiredService<IServiceScopeFactory>();
        var logger = NullLogger<AuditEntityBatchWriter>.Instance;
        var writer = new AuditEntityBatchWriter(scopeFactory, logger);

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "TestEntity",
            Action = AuditAction.Created,
            EntityId = Guid.NewGuid(),
            PropertyChanges = [new AuditEnvelopePropertyChange("Status", null, "Active")]
        };

        var outcomes1 = await writer.WriteBatchAsync([envelope], CancellationToken.None);
        var outcomes2 = await writer.WriteBatchAsync([envelope], CancellationToken.None);

        Assert.That(outcomes1, Has.Count.EqualTo(1));
        Assert.That(outcomes1[0].Succeeded, Is.True);
        Assert.That(outcomes2, Has.Count.EqualTo(1));
        Assert.That(outcomes2[0].Succeeded, Is.True);
        Assert.That(outcomes2[0].IsDuplicate, Is.True);

        // The stable envelope/property key makes replay idempotent.
        await using var context = new AuditDbContext(_options);
        var count = await context.AuditLogs.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    [Test]
    public async Task AuditEntityBatchWriter_MixedReplayAndNewEnvelope_PersistsNewEnvelope()
    {
        var writer = new AuditEntityBatchWriter(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditEntityBatchWriter>.Instance);
        var replay = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "ExistingEntity",
            Action = AuditAction.Updated,
            EntityId = Guid.NewGuid(),
            PropertyChanges = [new AuditEnvelopePropertyChange("Status", "Pending", "Active")]
        };
        var fresh = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "NewEntity",
            Action = AuditAction.Created,
            EntityId = Guid.NewGuid(),
            PropertyChanges = [new AuditEnvelopePropertyChange("Name", null, "New")]
        };
        await writer.WriteBatchAsync([replay], CancellationToken.None);

        var outcomes = await writer.WriteBatchAsync([replay, fresh], CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(outcomes.Single(outcome => outcome.EnvelopeId == replay.EnvelopeId).IsDuplicate, Is.True);
            Assert.That(outcomes.Single(outcome => outcome.EnvelopeId == fresh.EnvelopeId).IsDuplicate, Is.False);
        });
        await using var context = new AuditDbContext(_options);
        Assert.That(await context.AuditLogs.CountAsync(), Is.EqualTo(2));
    }

    #endregion

    #region DuplicateKeyDetector Integration

    [Test]
    public void DuplicateKeyDetector_SqliteUniqueViolation_ReturnsTrue()
    {
        // Create a duplicate key exception manually
        var innerEx = new SqliteException("UNIQUE constraint failed", 19);
        var dbUpdateEx = new DbUpdateException("Update failed", innerEx);

        var isDuplicate = DuplicateKeyDetector.IsDuplicateKey(dbUpdateEx);

        Assert.That(isDuplicate, Is.True);
    }

    [Test]
    public void DuplicateKeyDetector_OtherSqliteError_ReturnsFalse()
    {
        var innerEx = new SqliteException("NOT NULL constraint failed", 19);
        var dbUpdateEx = new DbUpdateException("Update failed", innerEx);

        var isDuplicate = DuplicateKeyDetector.IsDuplicateKey(dbUpdateEx);

        Assert.That(isDuplicate, Is.False);
    }

    #endregion
}
