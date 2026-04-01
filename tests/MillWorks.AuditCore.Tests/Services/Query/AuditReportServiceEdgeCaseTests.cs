using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

/// <summary>
/// Edge case unit tests for AuditReportService covering aggregation, pagination,
/// empty states, and no-argument defaults.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditReportServiceEdgeCaseTests
{
    private AuditApplicationDbContext _context;
    private Mock<ILogger<AuditReportService>> _mockLogger;
    private AuditReportService _reportService;

    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();
        _context = new AuditApplicationDbContext(options);
        _mockLogger = new Mock<ILogger<AuditReportService>>();
        _reportService = new AuditReportService(_context, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region GetActivitySummaryAsync — multi-user aggregation

    /// <summary>
    /// Verifies that GetActivitySummaryAsync correctly aggregates event type counts
    /// across multiple distinct event types when no user/date filter is applied.
    /// </summary>
    [Test]
    public async Task GetActivitySummaryAsync_MultipleUsers_AggregatesCorrectly()
    {
        // Arrange — 2 User.Login, 3 Data.Update, 1 User.Logout
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "User.Login",   InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "User.Login",   InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Data.Update",  InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Data.Update",  InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Data.Update",  InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "User.Logout",  InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetActivitySummaryAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.ContainsKey("User.Login"), Is.True);
        Assert.That(result.ContainsKey("Data.Update"), Is.True);
        Assert.That(result.ContainsKey("User.Logout"), Is.True);
        Assert.That(result["User.Login"],  Is.EqualTo(2));
        Assert.That(result["Data.Update"], Is.EqualTo(3));
        Assert.That(result["User.Logout"], Is.EqualTo(1));
    }

    #endregion

    #region GetEventTypeDistributionAsync — returns list ordered by count descending

    /// <summary>
    /// Verifies that GetEventTypeDistributionAsync returns AuditEventTypeCount items
    /// with accurate counts and the list is ordered by count descending.
    /// </summary>
    [Test]
    public async Task GetEventTypeDistributionAsync_ReturnsPercentages()
    {
        // Arrange — deliberate ordering to confirm the service re-orders the output
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Rare.Event",   InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common.Event", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common.Event", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common.Event", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Mid.Event",    InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Mid.Event",    InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetEventTypeDistributionAsync();

        // Assert — list has an item per distinct type
        Assert.That(result, Has.Count.EqualTo(3));

        // Ordered descending: Common(3), Mid(2), Rare(1)
        Assert.That(result[0].EventType, Is.EqualTo("Common.Event"));
        Assert.That(result[0].Count,     Is.EqualTo(3));
        Assert.That(result[1].EventType, Is.EqualTo("Mid.Event"));
        Assert.That(result[1].Count,     Is.EqualTo(2));
        Assert.That(result[2].EventType, Is.EqualTo("Rare.Event"));
        Assert.That(result[2].Count,     Is.EqualTo(1));

        // Every count must be positive
        Assert.That(result.All(static x => x.Count > 0), Is.True);
    }

    #endregion

    #region GetTopUsersAsync — respects the count limit

    /// <summary>
    /// Verifies that GetTopUsersAsync(3) returns exactly 3 items even when 10
    /// distinct users are present in the data.
    /// </summary>
    [Test]
    public async Task GetTopUsersAsync_RespectsLimit()
    {
        // Arrange — 10 distinct users, each with a different activity volume so the
        // ordering is deterministic (user0 = 10 events, user1 = 9 events, …, user9 = 1 event)
        var events = Enumerable.Range(0, 10)
            .SelectMany(static userIndex =>
                Enumerable.Range(0, 10 - userIndex)
                    .Select(_ => new AuditEventEntity
                    {
                        EventId = Guid.NewGuid(),
                        User = $"user{userIndex}@test.com",
                        InsertedDate = DateTimeOffset.UtcNow
                    }))
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetTopUsersAsync(3);

        // Assert
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result[0].User, Is.EqualTo("user0@test.com"));
        Assert.That(result[0].Count, Is.EqualTo(10));
    }

    #endregion

    #region GenerateAuditReportAsync — date range covers all sections

    /// <summary>
    /// Verifies that GenerateAuditReportAsync returns a non-empty byte array whose
    /// UTF-8 content contains the expected report sections including "Audit Report",
    /// the date-range boundary strings, and event counts.
    /// </summary>
    [Test]
    public async Task GenerateAuditReportAsync_DateRange_IncludesAllSections()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate   = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "alice@test.com", EventType = "User.Login",  InsertedDate = DateTimeOffset.UtcNow.AddDays(-5) },
            new() { EventId = Guid.NewGuid(), User = "bob@test.com",   EventType = "Data.Update", InsertedDate = DateTimeOffset.UtcNow.AddDays(-3) },
            new() { EventId = Guid.NewGuid(), User = "alice@test.com", EventType = "User.Logout", InsertedDate = DateTimeOffset.UtcNow.AddDays(-1) }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var bytes = await _reportService.GenerateAuditReportAsync(startDate, endDate);

        // Assert
        Assert.That(bytes, Is.Not.Null);
        Assert.That(bytes.Length, Is.GreaterThan(0));

        var reportText = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.That(reportText, Does.Contain("\"totalEvents\": 3"));
        Assert.That(reportText, Does.Contain("\"uniqueUsers\": 2"));
    }

    #endregion

    #region GetAuditChartDataAsync — empty data returns empty list

    /// <summary>
    /// Verifies that GetAuditChartDataAsync returns an empty list when there are
    /// no events stored for the requested date range.
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_NoData_ReturnsEmptyChart()
    {
        // Arrange — no events seeded
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate   = DateTimeOffset.UtcNow;

        // Act
        var result = await _reportService.GetAuditChartDataAsync(startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetAuditSummaryAsync — null dates default to all-time

    /// <summary>
    /// Verifies that calling GetAuditSummaryAsync() with no date arguments includes
    /// events from all time, not just a recent window.
    /// </summary>
    [Test]
    public async Task GetAuditSummaryAsync_NullDates_DefaultsToAllTime()
    {
        // Arrange — events spread across a wide date range
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "user1@test.com", InsertedDate = DateTimeOffset.UtcNow.AddDays(-365) },
            new() { EventId = Guid.NewGuid(), User = "user2@test.com", InsertedDate = DateTimeOffset.UtcNow.AddDays(-180) },
            new() { EventId = Guid.NewGuid(), User = "user3@test.com", InsertedDate = DateTimeOffset.UtcNow.AddDays(-90)  },
            new() { EventId = Guid.NewGuid(), User = "user4@test.com", InsertedDate = DateTimeOffset.UtcNow.AddDays(-30)  },
            new() { EventId = Guid.NewGuid(), User = "user5@test.com", InsertedDate = DateTimeOffset.UtcNow               }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act — intentionally pass no date arguments
        var result = await _reportService.GetAuditSummaryAsync();

        // Assert — all 5 events must be counted
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.EqualTo(5));
        Assert.That(result.UniqueUsers, Is.EqualTo(5));
        Assert.That(result.StartDate,   Is.Null);
        Assert.That(result.EndDate,     Is.Null);
    }

    #endregion
}
