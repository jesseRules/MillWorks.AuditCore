using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Repositories;

[TestFixture]
[Category("Unit")]
public class RepositoryOptimisticUpdateTests
{
    private AuditApplicationDbContext _context = null!;

    [SetUp]
    public void Setup()
    {
        _context = new AuditApplicationDbContext(TestDbContextFactory.CreateInMemoryOptions());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    [Test]
    public async Task ExecuteOptimisticUpdateAsync_SuccessOnFirstAttempt_UpdatesEntity()
    {
        var entity = CreateEntity("Original");
        var repository = new TestOptimisticRepository(_context, entity);

        var result = await repository.ExecuteOptimisticUpdateAsync(entity.EventId, e => e.EventType = "Updated");

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventType, Is.EqualTo("Updated"));
        Assert.That(repository.StoredEntity!.EventType, Is.EqualTo("Updated"));
        Assert.That(repository.GetByIdCallCount, Is.EqualTo(1));
        Assert.That(repository.SaveCallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ExecuteOptimisticUpdateAsync_ConcurrencyConflict_RetriesAndAppliesUpdatePerAttempt()
    {
        var entity = CreateEntity("Original");
        var repository = new TestOptimisticRepository(_context, entity);
        repository.QueueSaveException(new DbUpdateConcurrencyException("conflict"));

        var updateCalls = 0;

        var result = await repository.ExecuteOptimisticUpdateAsync(entity.EventId, e =>
        {
            updateCalls++;
            e.EventType = $"Updated-{updateCalls}";
        }, maxRetries: 3);

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventType, Is.EqualTo("Updated-2"));
        Assert.That(repository.StoredEntity!.EventType, Is.EqualTo("Updated-2"));
        Assert.That(updateCalls, Is.EqualTo(2));
        Assert.That(repository.GetByIdCallCount, Is.EqualTo(2));
        Assert.That(repository.SaveCallCount, Is.EqualTo(2));
    }

    [Test]
    public void ExecuteOptimisticUpdateAsync_MaxRetriesExceeded_ThrowsAndLeavesStoredEntityUnchanged()
    {
        var entity = CreateEntity("Original");
        var repository = new TestOptimisticRepository(_context, entity);
        repository.QueueSaveException(new DbUpdateConcurrencyException("conflict-1"));
        repository.QueueSaveException(new DbUpdateConcurrencyException("conflict-2"));

        var updateCalls = 0;

        Assert.ThrowsAsync<DbUpdateConcurrencyException>(async () =>
            await repository.ExecuteOptimisticUpdateAsync(entity.EventId, e =>
            {
                updateCalls++;
                e.EventType = "ShouldNotPersist";
            }, maxRetries: 2));

        Assert.That(updateCalls, Is.EqualTo(2));
        Assert.That(repository.StoredEntity!.EventType, Is.EqualTo("Original"));
        Assert.That(repository.GetByIdCallCount, Is.EqualTo(2));
        Assert.That(repository.SaveCallCount, Is.EqualTo(2));
    }

    [Test]
    public void ExecuteOptimisticUpdateAsync_UpdateActionThrows_PropagatesWithoutRetry()
    {
        var entity = CreateEntity("Original");
        var repository = new TestOptimisticRepository(_context, entity);

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await repository.ExecuteOptimisticUpdateAsync(entity.EventId, _ =>
            {
                throw new InvalidOperationException("boom");
            }));

        Assert.That(repository.GetByIdCallCount, Is.EqualTo(1));
        Assert.That(repository.SaveCallCount, Is.EqualTo(0));
        Assert.That(repository.StoredEntity!.EventType, Is.EqualTo("Original"));
    }

    [Test]
    public void ExecuteOptimisticUpdateAsync_CancelledBeforeFirstAttempt_ThrowsOperationCanceledException()
    {
        var entity = CreateEntity("Original");
        var repository = new TestOptimisticRepository(_context, entity);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await repository.ExecuteOptimisticUpdateAsync(entity.EventId, e => e.EventType = "Updated", cancellationToken: cts.Token));

        Assert.That(repository.GetByIdCallCount, Is.EqualTo(0));
        Assert.That(repository.SaveCallCount, Is.EqualTo(0));
    }

    private static AuditEventEntity CreateEntity(string eventType) =>
        new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            InsertedDate = DateTimeOffset.UtcNow
        };

    private sealed class TestOptimisticRepository(AuditApplicationDbContext context, AuditEventEntity seedEntity)
        : Repository<AuditEventEntity>(context)
    {
        private readonly Queue<Exception?> _saveOutcomes = new();

        public int GetByIdCallCount { get; private set; }
        public int SaveCallCount { get; private set; }
        public AuditEventEntity? StoredEntity { get; private set; } = Clone(seedEntity);

        public void QueueSaveException(Exception exception)
        {
            _saveOutcomes.Enqueue(exception);
        }

        public override Task<AuditEventEntity?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetByIdCallCount++;

            if (StoredEntity is null || StoredEntity.EventId != id)
                return Task.FromResult<AuditEventEntity?>(null);

            return Task.FromResult<AuditEventEntity?>(Clone(StoredEntity));
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;

            if (_saveOutcomes.Count > 0)
            {
                var next = _saveOutcomes.Dequeue();
                if (next is not null)
                    throw next;
            }

            var trackedEntity = Context.ChangeTracker.Entries<AuditEventEntity>().Single().Entity;
            StoredEntity = Clone(trackedEntity);
            return Task.FromResult(1);
        }

        private static AuditEventEntity Clone(AuditEventEntity source) =>
            new()
            {
                EventId = source.EventId,
                EventType = source.EventType,
                InsertedDate = source.InsertedDate,
                JsonData = source.JsonData,
                User = source.User
            };
    }
}
