using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for Repository&lt;T&gt; transaction management methods.
/// Note: InMemory provider ignores transactions, so these tests verify control flow
/// (_currentTransaction state, no exceptions) rather than actual transactional behavior.
/// </summary>
[TestFixture]
public class RepositoryTransactionTests
{
    private DbContextOptions<AuditApplicationDbContext> _options;
    private AuditApplicationDbContext _context;
    private AuditEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(_options);
        _repository = new AuditEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region ExecuteInTransactionAsync (void)

    /// <summary>
    /// Verifies ExecuteInTransactionAsync commits on success.
    /// </summary>
    [Test]
    public async Task ExecuteInTransactionAsync_OnSuccess_CommitsWithoutError()
    {
        // Arrange
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "TxTest",
            InsertedDate = DateTimeOffset.UtcNow
        };

        // Act
        await _repository.ExecuteInTransactionAsync(async () =>
        {
            await _repository.AddAsync(entity);
            await _repository.SaveChangesAsync();
        });

        // Assert - entity was persisted
        var saved = await _context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// Verifies ExecuteInTransactionAsync rolls back on exception.
    /// </summary>
    [Test]
    public void ExecuteInTransactionAsync_OnException_RollsBackAndRethrows()
    {
        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _repository.ExecuteInTransactionAsync(static () =>
                throw new InvalidOperationException("Test failure"));
        });

        Assert.That(ex!.Message, Is.EqualTo("Test failure"));
    }

    /// <summary>
    /// Verifies that after ExecuteInTransactionAsync completes, a new transaction can be started
    /// (validates bug fix: _currentTransaction is reset to null).
    /// </summary>
    [Test]
    public async Task ExecuteInTransactionAsync_AfterCompletion_AllowsNewTransaction()
    {
        // Act - first transaction
        await _repository.ExecuteInTransactionAsync(async () =>
        {
            await _repository.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Tx1",
                InsertedDate = DateTimeOffset.UtcNow
            });
            await _repository.SaveChangesAsync();
        });

        // Act - second transaction should not throw
        await _repository.ExecuteInTransactionAsync(async () =>
        {
            await _repository.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Tx2",
                InsertedDate = DateTimeOffset.UtcNow
            });
            await _repository.SaveChangesAsync();
        });

        // Assert - both entities persisted
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies that after ExecuteInTransactionAsync fails, a new transaction can still be started
    /// (validates bug fix: _currentTransaction is reset even on exception).
    /// </summary>
    [Test]
    public async Task ExecuteInTransactionAsync_AfterFailure_AllowsNewTransaction()
    {
        // Act - first transaction fails
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await _repository.ExecuteInTransactionAsync(static () =>
                throw new InvalidOperationException("Fail"));
        });

        // Act - second transaction should not throw
        await _repository.ExecuteInTransactionAsync(async () =>
        {
            await _repository.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "AfterFail",
                InsertedDate = DateTimeOffset.UtcNow
            });
            await _repository.SaveChangesAsync();
        });

        // Assert
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    #endregion

    #region ExecuteInTransactionAsync<TResult>

    /// <summary>
    /// Verifies ExecuteInTransactionAsync&lt;TResult&gt; returns result on success.
    /// </summary>
    [Test]
    public async Task ExecuteInTransactionAsync_WithResult_ReturnsResultOnSuccess()
    {
        // Act
        var result = await _repository.ExecuteInTransactionAsync(async () =>
        {
            var entity = new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "TxResult",
                InsertedDate = DateTimeOffset.UtcNow
            };
            await _repository.AddAsync(entity);
            await _repository.SaveChangesAsync();
            return entity.EventId;
        });

        // Assert
        Assert.That(result, Is.Not.EqualTo(Guid.Empty));
        var saved = await _context.AuditEvents.FindAsync(result);
        Assert.That(saved, Is.Not.Null);
    }

    /// <summary>
    /// Verifies ExecuteInTransactionAsync&lt;TResult&gt; allows new transaction after completion.
    /// </summary>
    [Test]
    public async Task ExecuteInTransactionAsync_WithResult_AfterCompletion_AllowsNewTransaction()
    {
        // Act - first
        var id1 = await _repository.ExecuteInTransactionAsync(async () =>
        {
            var entity = new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Tx1",
                InsertedDate = DateTimeOffset.UtcNow
            };
            await _repository.AddAsync(entity);
            await _repository.SaveChangesAsync();
            return entity.EventId;
        });

        // Act - second should not throw
        var id2 = await _repository.ExecuteInTransactionAsync(async () =>
        {
            var entity = new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = "Tx2",
                InsertedDate = DateTimeOffset.UtcNow
            };
            await _repository.AddAsync(entity);
            await _repository.SaveChangesAsync();
            return entity.EventId;
        });

        // Assert
        Assert.That(id1, Is.Not.EqualTo(id2));
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(2));
    }

    #endregion

    #region BeginTransactionAsync

    /// <summary>
    /// Verifies BeginTransactionAsync throws if a transaction is already in progress.
    /// </summary>
    [Test]
    public async Task BeginTransactionAsync_WhenTransactionInProgress_Throws()
    {
        // Arrange
        var tx = await _repository.BeginTransactionAsync();

        // Act & Assert
        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _repository.BeginTransactionAsync());
        Assert.That(ex!.Message, Does.Contain("transaction is already in progress"));

        // Cleanup
        await tx.DisposeAsync();
    }

    #endregion

    #region CommitTransactionAsync

    /// <summary>
    /// Verifies CommitTransactionAsync saves changes and commits.
    /// </summary>
    [Test]
    public async Task CommitTransactionAsync_SavesAndCommits()
    {
        // Arrange
        await _repository.BeginTransactionAsync();
        await _repository.AddAsync(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "CommitTest",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Act
        await _repository.CommitTransactionAsync();

        // Assert
        Assert.That(_repository.CurrentTransaction, Is.Null);
        var count = await _context.AuditEvents.CountAsync();
        Assert.That(count, Is.EqualTo(1));
    }

    #endregion

    #region RollbackTransactionAsync

    /// <summary>
    /// Verifies RollbackTransactionAsync rolls back without error.
    /// </summary>
    [Test]
    public async Task RollbackTransactionAsync_RollsBackWithoutError()
    {
        // Arrange
        await _repository.BeginTransactionAsync();

        // Act
        await _repository.RollbackTransactionAsync();

        // Assert - no exception thrown and transaction is cleared
        Assert.That(_repository.CurrentTransaction, Is.Null);
    }

    /// <summary>
    /// Verifies RollbackTransactionAsync is safe to call with no current transaction.
    /// </summary>
    [Test]
    public void RollbackTransactionAsync_WithNoTransaction_DoesNotThrow()
    {
        // Act & Assert
        Assert.DoesNotThrowAsync(async () => await _repository.RollbackTransactionAsync());
    }

    #endregion
}
