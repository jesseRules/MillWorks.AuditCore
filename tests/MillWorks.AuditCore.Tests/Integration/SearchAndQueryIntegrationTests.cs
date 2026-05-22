using Mapster;
using MapsterMapper;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Mapping;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for AuditSearchService, AuditReportService, and AuditQueryService
/// verifying full-text search, summaries, chart data, entity trails, and pagination
/// against a real SQLite backend.
/// </summary>
[TestFixture]
[Category("Integration")]
public class SearchAndQueryIntegrationTests : SqliteIntegrationFixture
{
    private IMapper _mapper = null!;

    [OneTimeSetUp]
    public void SetupMapper()
    {
        var config = new TypeAdapterConfig();
        new AuditMappingConfiguration().Register(config);
        _mapper = new Mapper(config);
    }

    private static AuditEventEntity CreateAuditEvent(
        string eventType = "User.Login",
        string user = "alice@test.com",
        DateTimeOffset? insertedDate = null,
        string? jsonData = null,
        string? entityType = null,
        string? entityId = null)
    {
        return new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            User = user,
            UserFullName = $"{user} FullName",
            UserId = Guid.NewGuid(),
            JsonData = jsonData ?? """{"action":"test"}""",
            InsertedDate = insertedDate ?? DateTimeOffset.UtcNow,
            EntityType = entityType ?? "User",
            EntityId = entityId ?? Guid.NewGuid().ToString()
        };
    }

    [Test]
    public async Task SearchAuditEvents_FullTextSearch_ReturnsMatches()
    {
        // Arrange
        using var context = CreateContext();
        await context.AuditEvents.AddRangeAsync(
            CreateAuditEvent(eventType: "User.Login", user: "alice@test.com",
                jsonData: """{"action":"login","method":"password"}"""),
            CreateAuditEvent(eventType: "User.Logout", user: "bob@test.com",
                jsonData: """{"action":"logout"}"""),
            CreateAuditEvent(eventType: "Order.Created", user: "charlie@test.com",
                jsonData: """{"action":"create","item":"widget"}"""),
            CreateAuditEvent(eventType: "User.PasswordReset", user: "alice@test.com",
                jsonData: """{"action":"password_reset","method":"email"}"""));
        await context.SaveChangesAsync();

        var service = new AuditSearchService(context, _mapper, NullLogger<AuditSearchService>.Instance);

        // Act - search for "alice" which should match User field
        var result = await service.SearchAuditEventsAsync(new AuditSearchRequest
        {
            SearchTerm = "alice",
            Limit = 50
        });

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(2));
        Assert.That(result.Items, Has.Count.EqualTo(2));
        Assert.That(result.Items!.All(static i => i.User == "alice@test.com"), Is.True);
    }

    [Test]
    public async Task GetAuditSummary_WithData_ReturnsAccurateSummary()
    {
        // Arrange
        using var context = CreateContext();
        await context.AuditEvents.AddRangeAsync(
            CreateAuditEvent(eventType: "User.Login", user: "alice@test.com"),
            CreateAuditEvent(eventType: "User.Login", user: "bob@test.com"),
            CreateAuditEvent(eventType: "User.Logout", user: "alice@test.com"),
            CreateAuditEvent(eventType: "Order.Created", user: "charlie@test.com"),
            CreateAuditEvent(eventType: "Order.Updated", user: "alice@test.com"));
        await context.SaveChangesAsync();

        var service = new AuditReportService(context, NullLogger<AuditReportService>.Instance);

        // Act
        var summary = await service.GetAuditSummaryAsync();

        // Assert
        Assert.That(summary.TotalEvents, Is.EqualTo(5));
        Assert.That(summary.UniqueUsers, Is.EqualTo(3));
        Assert.That(summary.EventTypes, Is.Not.Empty);
        Assert.That(summary.TopUsers, Is.Not.Empty);
    }

    [Test]
    public async Task GetAuditChartData_GroupsByTimePeriod()
    {
        // Arrange
        using var context = CreateContext();
        var now = DateTimeOffset.UtcNow;

        // Seed events across 3 different days
        await context.AuditEvents.AddRangeAsync(
            CreateAuditEvent(insertedDate: now),
            CreateAuditEvent(insertedDate: now),
            CreateAuditEvent(insertedDate: now.AddDays(-1)),
            CreateAuditEvent(insertedDate: now.AddDays(-1)),
            CreateAuditEvent(insertedDate: now.AddDays(-1)),
            CreateAuditEvent(insertedDate: now.AddDays(-2)));
        await context.SaveChangesAsync();

        var service = new AuditReportService(context, NullLogger<AuditReportService>.Instance);

        // Act
        var response = await service.GetAuditChartDataAsync(
            now.AddDays(-3), now.AddDays(1), groupBy: "day");

        // Assert
        Assert.That(response.Items, Is.Not.Empty);
        Assert.That(response.Items, Has.Count.EqualTo(3)); // 3 distinct days
        Assert.That(response.Items.Sum(static d => d.Count), Is.EqualTo(6));

        // Verify ordering is chronological
        for (int i = 1; i < response.Items.Count; i++)
        {
            Assert.That(response.Items[i].Date, Is.GreaterThanOrEqualTo(response.Items[i - 1].Date));
        }
    }

    [Test]
    public async Task GetEntityAuditTrail_ReturnsChronologicalHistory()
    {
        // Arrange
        using var context = CreateContext();
        var entityId = Guid.NewGuid();

        await context.AuditEvents.AddRangeAsync(
            CreateAuditEvent(eventType: "Order.Created", entityType: "Order",
                entityId: entityId.ToString(),
                insertedDate: DateTimeOffset.UtcNow.AddHours(-3)),
            CreateAuditEvent(eventType: "Order.Updated", entityType: "Order",
                entityId: entityId.ToString(),
                insertedDate: DateTimeOffset.UtcNow.AddHours(-2)),
            CreateAuditEvent(eventType: "Order.Shipped", entityType: "Order",
                entityId: entityId.ToString(),
                insertedDate: DateTimeOffset.UtcNow.AddHours(-1)),
            // Different entity - should not appear in results
            CreateAuditEvent(eventType: "Order.Created", entityType: "Order",
                entityId: Guid.NewGuid().ToString()));
        await context.SaveChangesAsync();

        var service = new AuditQueryService(context, _mapper, NullLogger<AuditQueryService>.Instance);

        // Act
        var trail = (await service.GetEntityAuditTrailAsync("Order", entityId)).ToList();

        // Assert
        Assert.That(trail, Has.Count.EqualTo(3));

        // GetEntityAuditTrailAsync orders by InsertedDate descending (newest first)
        Assert.That(trail[0].CreatedAt, Is.GreaterThanOrEqualTo(trail[1].CreatedAt));
        Assert.That(trail[1].CreatedAt, Is.GreaterThanOrEqualTo(trail[2].CreatedAt));
    }

    [Test]
    public async Task PaginatedSearch_MultiplePages_AllResultsReturned()
    {
        // Arrange
        using var context = CreateContext();
        for (int i = 0; i < 30; i++)
        {
            await context.AuditEvents.AddAsync(CreateAuditEvent(
                eventType: "Bulk.Event",
                user: $"user{i}@test.com",
                insertedDate: DateTimeOffset.UtcNow.AddMinutes(-i)));
        }
        await context.SaveChangesAsync();

        var service = new AuditSearchService(context, _mapper, NullLogger<AuditSearchService>.Instance);

        // Act - first page of 10
        var result = await service.SearchAuditEventsAsync(new AuditSearchRequest
        {
            Limit = 10,
            Offset = 0
        });

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(30));
        Assert.That(result.Items, Has.Count.EqualTo(10));
        Assert.That(result.TotalPages, Is.EqualTo(3));
        Assert.That(result.CurrentPage, Is.EqualTo(1));
        Assert.That(result.Limit, Is.EqualTo(10));
        Assert.That(result.Offset, Is.EqualTo(0));

        // Act - second page
        var page2 = await service.SearchAuditEventsAsync(new AuditSearchRequest
        {
            Limit = 10,
            Offset = 10
        });

        // Assert
        Assert.That(page2.TotalItems, Is.EqualTo(30));
        Assert.That(page2.Items, Has.Count.EqualTo(10));
        Assert.That(page2.CurrentPage, Is.EqualTo(2));

        // Verify no overlap between pages
        var page1Ids = result.Items!.Select(static i => i.EventId).ToHashSet();
        var page2Ids = page2.Items!.Select(static i => i.EventId).ToHashSet();
        Assert.That(page1Ids.Intersect(page2Ids), Is.Empty);
    }
}
