using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests that Repository&lt;T&gt;.Dispose does NOT dispose the injected DbContext,
/// since the context lifetime is managed by the DI container.
/// </summary>
[TestFixture]
public class RepositoryDisposeTests
{
    /// <summary>
    /// Verifies that after disposing the repository, the DbContext is still usable.
    /// </summary>
    [Test]
    public async Task Dispose_DoesNotDisposeInjectedDbContext()
    {
        // Arrange
        var options = TestDbContextFactory.CreateInMemoryOptions();

        var context = new AuditDbContext(options);
        var repository = new AuditEventRepository(context);

        // Seed data via repo
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Dispose",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await repository.AddAsync(entity);
        await repository.SaveChangesAsync();

        // Act — dispose the repository
        repository.Dispose();

        // Assert — context should still be usable
        var found = await context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.EventType, Is.EqualTo("Test.Dispose"));

        // Cleanup
        context.Dispose();
    }

    /// <summary>
    /// Verifies that after DisposeAsync, the DbContext is still usable.
    /// </summary>
    [Test]
    public async Task DisposeAsync_DoesNotDisposeInjectedDbContext()
    {
        // Arrange
        var options = TestDbContextFactory.CreateInMemoryOptions();

        var context = new AuditDbContext(options);
        var repository = new AuditEventRepository(context);

        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.DisposeAsync",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await repository.AddAsync(entity);
        await repository.SaveChangesAsync();

        // Act — dispose the repository asynchronously
        await repository.DisposeAsync();

        // Assert — context should still be usable
        var found = await context.AuditEvents.FindAsync(entity.EventId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.EventType, Is.EqualTo("Test.DisposeAsync"));

        // Cleanup
        await context.DisposeAsync();
    }

    /// <summary>
    /// Verifies that a second repository sharing the same context still works after the first is disposed.
    /// </summary>
    [Test]
    public async Task Dispose_SharedContext_SecondRepositoryStillWorks()
    {
        // Arrange
        var options = TestDbContextFactory.CreateInMemoryOptions();

        var context = new AuditDbContext(options);
        var repo1 = new AuditEventRepository(context);
        var repo2 = new AuditEventRepository(context);

        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.SharedContext",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await repo1.AddAsync(entity);
        await repo1.SaveChangesAsync();

        // Act — dispose repo1
        repo1.Dispose();

        // Assert — repo2 should still work
        var found = await repo2.GetByIdAsync(entity.EventId);
        Assert.That(found, Is.Not.Null);
        Assert.That(found!.EventType, Is.EqualTo("Test.SharedContext"));

        // Cleanup
        repo2.Dispose();
        context.Dispose();
    }
}
