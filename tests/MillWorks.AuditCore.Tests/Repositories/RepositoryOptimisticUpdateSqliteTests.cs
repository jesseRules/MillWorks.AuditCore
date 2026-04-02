using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

[TestFixture]
[Category("Unit")]
public class RepositoryOptimisticUpdateSqliteTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AuditApplicationDbContext> _options = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Dispose();
    }

    [Test]
    public async Task SaveChangesAsync_AssignsAndRefreshesRowVersion()
    {
        await using var context = CreateContext();

        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Created",
            JsonData = "{}",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await context.AuditEvents.AddAsync(entity);
        await context.SaveChangesAsync();
        var initialRowVersion = entity.RowVersion.ToArray();

        entity.EventType = "Updated";
        await context.SaveChangesAsync();

        Assert.That(initialRowVersion, Is.Not.Empty);
        Assert.That(entity.RowVersion, Is.Not.Empty);
        Assert.That(entity.RowVersion, Is.Not.EqualTo(initialRowVersion));
    }

    [Test]
    public async Task ExecuteOptimisticUpdateAsync_WhenConcurrentWriterWins_RetriesAgainstFreshRowVersion()
    {
        var eventId = await SeedEventAsync();

        await using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var updateCalls = 0;
        var externalConflictApplied = false;

        var result = await repository.ExecuteOptimisticUpdateAsync(eventId, entity =>
        {
            updateCalls++;
            entity.EventType = $"Updated-{updateCalls}";

            if (externalConflictApplied)
                return;

            using var concurrentContext = CreateContext();
            var concurrentEntity = concurrentContext.AuditEvents.Single(e => e.EventId == eventId);
            concurrentEntity.EventType = "Concurrent-Update";
            concurrentContext.SaveChanges();
            externalConflictApplied = true;
        }, maxRetries: 3);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventType, Is.EqualTo("Updated-2"));
        Assert.That(updateCalls, Is.EqualTo(2));

        await using var assertContext = CreateContext();
        var persisted = await assertContext.AuditEvents.SingleAsync(e => e.EventId == eventId);
        Assert.That(persisted.EventType, Is.EqualTo("Updated-2"));
    }

    [Test]
    public void ExecuteOptimisticUpdateAsync_WhenEveryAttemptConflicts_ThrowsDbUpdateConcurrencyException()
    {
        var eventId = SeedEventAsync().GetAwaiter().GetResult();

        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var updateCalls = 0;

        Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
            await repository.ExecuteOptimisticUpdateAsync(eventId, entity =>
            {
                updateCalls++;
                entity.EventType = $"Updated-{updateCalls}";

                using var concurrentContext = CreateContext();
                var concurrentEntity = concurrentContext.AuditEvents.Single(e => e.EventId == eventId);
                concurrentEntity.EventType = $"Concurrent-{updateCalls}";
                concurrentContext.SaveChanges();
            }, maxRetries: 2));

        Assert.That(updateCalls, Is.EqualTo(2));

        using var assertContext = CreateContext();
        var persisted = assertContext.AuditEvents.Single(e => e.EventId == eventId);
        Assert.That(persisted.EventType, Is.EqualTo("Concurrent-2"));
    }

    private AuditApplicationDbContext CreateContext() => new(_options);

    private async Task<Guid> SeedEventAsync()
    {
        await using var context = CreateContext();
        var eventId = Guid.NewGuid();
        await context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Initial",
            JsonData = "{}",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();
        return eventId;
    }
}
