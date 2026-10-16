using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using Moq;

namespace MillWorks.AuditCore.Tests.Repositories;

[TestFixture]
public sealed class RepositoryTransactionFailureTests
{
    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(false, true)]
    [TestCase(true, true)]
    public async Task OperationFailure_SurvivesRollbackAndDisposalFailures(bool withResult, bool canceled)
    {
        await using var context = new AuditDbContext(new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var transaction = new Mock<IDbContextTransaction>();
        var rollbackFailure = new InvalidOperationException("This transaction has completed");
        var disposalFailure = new InvalidOperationException("Connection is closed");
        transaction.Setup(t => t.RollbackAsync(It.IsAny<CancellationToken>())).ThrowsAsync(rollbackFailure);
        transaction.Setup(t => t.DisposeAsync()).Returns(new ValueTask(Task.FromException(disposalFailure)));
        var repository = new TestRepository(context, transaction.Object);
        using var cancellation = new CancellationTokenSource();
        Exception original = canceled ? new OperationCanceledException(cancellation.Token) : new TimeoutException("SQL stalled");
        if (canceled) cancellation.Cancel();
        Exception? observed = null;
        try
        {
            if (withResult)
                await repository.ExecuteInTransactionAsync<int>(() => Task.FromException<int>(original), cancellation.Token);
            else
                await repository.ExecuteInTransactionAsync(() => Task.FromException(original), cancellation.Token);
        }
        catch (Exception ex) { observed = ex; }

        Assert.That(observed, Is.SameAs(original));
        Assert.That(original.Data["TransactionRollbackException"], Is.SameAs(rollbackFailure));
        Assert.That(original.Data["TransactionDisposalException"], Is.SameAs(disposalFailure));
        transaction.Verify(t => t.RollbackAsync(CancellationToken.None), Times.Once);
        transaction.Verify(t => t.DisposeAsync(), Times.Once);
    }

    private sealed class TestRepository(AuditDbContext context, IDbContextTransaction transaction)
        : Repository<AuditEventEntity>(context)
    {
        public override Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(transaction);
    }
}
