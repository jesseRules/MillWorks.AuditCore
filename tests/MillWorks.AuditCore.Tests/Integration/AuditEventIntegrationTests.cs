using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for AuditEventRepository methods that require a relational database
/// (GroupBy projections).
/// </summary>
[TestFixture]
[Category("Integration")]
public class AuditEventIntegrationTests : SqliteIntegrationFixture
{
    #region GetEventTypeCountsAsync

    [Test]
    public async Task GetEventTypeCountsAsync_ReturnsGroupedCounts()
    {
        using var context = CreateContext();
        var repository = new AuditEventRepository(context);

        await context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Login", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Logout", InsertedDate = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var counts = await repository.GetEventTypeCountsAsync();

        Assert.That(counts, Has.Count.EqualTo(2));
        Assert.That(counts[0].Key, Is.EqualTo("Login"));
        Assert.That(counts[0].Value, Is.EqualTo(2));
    }

    #endregion

    #region GetTopUsersByActivityAsync

    [Test]
    public async Task GetTopUsersByActivityAsync_ReturnsTopUsers()
    {
        using var context = CreateContext();
        var repository = new AuditEventRepository(context);

        for (int i = 0; i < 5; i++)
            await context.AuditEvents.AddAsync(new AuditEventEntity { EventId = Guid.NewGuid(), User = "alice", EventType = "A", InsertedDate = DateTimeOffset.UtcNow });
        for (int i = 0; i < 3; i++)
            await context.AuditEvents.AddAsync(new AuditEventEntity { EventId = Guid.NewGuid(), User = "bob", EventType = "A", InsertedDate = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var top = await repository.GetTopUsersByActivityAsync(take: 1);

        Assert.That(top, Has.Count.EqualTo(1));
        Assert.That(top[0].Key, Is.EqualTo("alice"));
        Assert.That(top[0].Value, Is.EqualTo(5));
    }

    #endregion
}
