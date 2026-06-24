using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// End-to-end integration tests for AuditQueryService using SQLite.
/// Validates pagination, date range queries, and ordering against a real relational backend.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AuditQueryServiceIntegrationTests : SqliteIntegrationFixture
{
    [Test]
    public async Task GetAuditEventsAsync_Pagination_ReturnsCorrectPageAndMetadata()
    {
        // Arrange
        using var context = CreateContext();
        for (int i = 0; i < 25; i++)
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Page.Event{i}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await context.SaveChangesAsync();

        var service = new AuditQueryService(context, NullLogger<AuditQueryService>.Instance);

        // Act — first page of 10
        var response = await service.GetAuditEventsAsync(offset: 0, limit: 10);

        // Assert
        Assert.That(response.TotalItems, Is.EqualTo(25));
        Assert.That(response.Items, Has.Count.EqualTo(10));
        Assert.That(response.TotalPages, Is.EqualTo(3));
        Assert.That(response.CurrentPage, Is.EqualTo(1));
    }

    [Test]
    public async Task GetEntityAuditTrailAsync_ReturnsOnlyMatchingEntity()
    {
        // Arrange
        using var context = CreateContext();
        var entityId = Guid.NewGuid();
        var otherId = Guid.NewGuid();

        await context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = entityId.ToString(), EventType = "Order.Created", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = entityId.ToString(), EventType = "Order.Updated", InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-1) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EntityType = "Order", EntityId = otherId.ToString(), EventType = "Order.Created", InsertedDate = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();

        var service = new AuditQueryService(context, NullLogger<AuditQueryService>.Instance);

        // Act
        var trail = (await service.GetEntityAuditTrailAsync("Order", entityId)).ToList();

        // Assert
        Assert.That(trail, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task GetRecentActivityAsync_ReturnsNewestFirst()
    {
        // Arrange
        using var context = CreateContext();
        await context.AuditEvents.AddRangeAsync(
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Old.Event", InsertedDate = DateTimeOffset.UtcNow.AddHours(-2) },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "New.Event", InsertedDate = DateTimeOffset.UtcNow },
            new AuditEventEntity { EventId = Guid.NewGuid(), EventType = "Mid.Event", InsertedDate = DateTimeOffset.UtcNow.AddHours(-1) });
        await context.SaveChangesAsync();

        var service = new AuditQueryService(context, NullLogger<AuditQueryService>.Instance);

        // Act
        var results = (await service.GetRecentActivityAsync(hours: 24)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results[0].CreatedAt, Is.GreaterThanOrEqualTo(results[1].CreatedAt));
        Assert.That(results[1].CreatedAt, Is.GreaterThanOrEqualTo(results[2].CreatedAt));
    }
}
