using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for base Repository methods that require a relational database
/// (ExecuteDeleteAsync).
/// </summary>
[TestFixture]
[Category("Integration")]
public class RepositoryBaseIntegrationTests : SqliteIntegrationFixture
{
    [Test]
    public async Task ExecuteDeleteWhereAsync_WithMatchingPredicate_DeletesMatching()
    {
        using var context = CreateContext();
        var repository = new AuditEventRepository(context);

        await context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Keep", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Delete", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Delete", InsertedDate = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var deletedCount = await repository.ExecuteDeleteWhereAsync(static e => e.EventType == "Delete");

        Assert.That(deletedCount, Is.EqualTo(2));
        var remaining = await context.AuditEvents.ToListAsync();
        Assert.That(remaining, Has.Count.EqualTo(1));
        Assert.That(remaining[0].EventType, Is.EqualTo("Keep"));
    }
}
