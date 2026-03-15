using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

/// <summary>
/// Unit tests for AuditReportService
/// </summary>
[TestFixture]
public class AuditReportServiceTests
{
    /// <summary>
    /// Context
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<AuditReportService>> _mockLogger;

    /// <summary>
    /// Report Service
    /// </summary>
    private AuditReportService _reportService;

    /// <summary>
    /// Setup
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(options);
        _mockLogger = new Mock<ILogger<AuditReportService>>();

        _reportService = new AuditReportService(
            _context,
            _mockLogger.Object);
    }

    /// <summary>
    /// Tear down
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region GetAuditSummaryAsync Tests

    /// <summary>
    /// Get Audit Summary
    /// </summary>
    [Test]
    public async Task GetAuditSummaryAsync_WithValidDateRange_ReturnsSummary()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                User = "user1@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                User = "user2@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Data.Update",
                User = "user1@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-1)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditSummaryAsync(startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.EqualTo(3));
        Assert.That(result.UniqueUsers, Is.EqualTo(2));
        Assert.That(result.StartDate, Is.EqualTo(startDate));
        Assert.That(result.EndDate, Is.EqualTo(endDate));
        Assert.That(result.EventTypes, Is.Not.Empty);
        Assert.That(result.TopUsers, Is.Not.Empty);
    }

    /// <summary>
    /// Get Audit Summary without dates
    /// </summary>
    [Test]
    public async Task GetAuditSummaryAsync_WithoutDates_IncludesAllEvents()
    {
        // Arrange
        var events = Enumerable.Range(0, 50)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.Type{i % 5}",
                User = $"user{i % 10}@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditSummaryAsync();

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(50));
        Assert.That(result.UniqueUsers, Is.EqualTo(10));
    }

    /// <summary>
    /// Get Audit Summary with only start date
    /// </summary>
    [Test]
    public async Task GetAuditSummaryAsync_WithStartDateOnly_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-5);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = "user1@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) // Before start
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "user2@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3) // After start
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditSummaryAsync(startDate: startDate);

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(1));
    }

    /// <summary>
    /// Get Audit Summary with only end date
    /// </summary>
    [Test]
    public async Task GetAuditSummaryAsync_WithEndDateOnly_FiltersCorrectly()
    {
        // Arrange
        var endDate = DateTimeOffset.UtcNow.AddDays(-5);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = "user1@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) // Before end
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "user2@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-1) // After end
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditSummaryAsync(endDate: endDate);

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(1));
    }

    #endregion

    #region GetAuditChartDataAsync Tests

    /// <summary>
    /// Get Audit Chart Data grouped by day
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByDay_ReturnsCorrectGroups()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7).Date;
        var endDate = DateTimeOffset.UtcNow.Date;

        var events = Enumerable.Range(0, 7)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = startDate.AddDays(i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Count, Is.LessThanOrEqualTo(8)); // 7 days + possible edge
        Assert.That(result.All(static x => x.Count >= 0), Is.True);
    }

    /// <summary>
    /// Get Audit Chart Data grouped by hour
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByHour_ReturnsHourlyData()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddHours(-24);
        var endDate = DateTimeOffset.UtcNow;

        var events = Enumerable.Range(0, 24)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = startDate.AddHours(i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "hour");

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(static x => x.Label.Contains(":")), Is.True); // Hour format includes :
    }

    /// <summary>
    /// Get Audit Chart Data grouped by week
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByWeek_ReturnsWeeklyData()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-30).Date;
        var endDate = DateTimeOffset.UtcNow.Date;

        var events = Enumerable.Range(0, 30)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = startDate.AddDays(i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "week");

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.All(static x => x.Label.Contains("Week")), Is.True);
    }

    /// <summary>
    /// Get Audit Chart Data grouped by month
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByMonth_ReturnsMonthlyData()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddMonths(-6).Date;
        var endDate = DateTimeOffset.UtcNow.Date;

        var events = Enumerable.Range(0, 180)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = startDate.AddDays(i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "month");

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Count, Is.LessThanOrEqualTo(7)); // 6 months + possible edge
    }

    /// <summary>
    /// Get Audit Chart Data grouped by user
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByUser_ReturnsTopUsers()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var events = Enumerable.Range(0, 25)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                User = $"user{i % 5}@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-i % 7)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "user");

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.Count, Is.LessThanOrEqualTo(20)); // Top 20 users
        Assert.That(result.All(static x => !string.IsNullOrEmpty(x.User)), Is.True);
    }

    /// <summary>
    /// Get Audit Chart Data grouped by event type
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_GroupedByEventType_ReturnsEventTypes()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-4)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Data.Update",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "eventtype");

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.Any(static x => x is { EventType: "User.Login", Count: 2 }), Is.True);
        Assert.That(result.Any(static x => x is { EventType: "Data.Update", Count: 1 }), Is.True);
    }

    /// <summary>
    /// Get Audit Chart Data with invalid group by defaults to day
    /// </summary>
    [Test]
    public async Task GetAuditChartDataAsync_WithInvalidGroupBy_DefaultsToDay()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetAuditChartDataAsync(
            startDate, endDate, "invalid");

        // Assert
        Assert.That(result, Is.Not.Empty);
    }

    #endregion

    #region GetActivitySummaryAsync Tests

    /// <summary>
    /// Get Activity Summary with UserId filters to that user
    /// </summary>
    [Test]
    public async Task GetActivitySummaryAsync_WithUserId_FiltersToUser()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = otherUserId,
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetActivitySummaryAsync(userId);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey("User.Login"), Is.True);
    }

    /// <summary>
    /// Get Activity Summary with FromDate filters correctly
    /// </summary>
    [Test]
    public async Task GetActivitySummaryAsync_WithFromDate_FiltersCorrectly()
    {
        // Arrange
        var fromDate = DateTimeOffset.UtcNow.AddDays(-5);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Recent.Event",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-2)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Old.Event",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetActivitySummaryAsync(fromDate: fromDate);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.ContainsKey("Recent.Event"), Is.True);
    }

    /// <summary>
    /// Get Activity Summary grouped by event type counts correctly
    /// </summary>
    [Test]
    public async Task GetActivitySummaryAsync_GroupsByEventType_CountsCorrectly()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Data.Update",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetActivitySummaryAsync();

        // Assert
        Assert.That(result["User.Login"], Is.EqualTo(2));
        Assert.That(result["Data.Update"], Is.EqualTo(1));
    }

    #endregion

    #region GetEventTypeDistributionAsync Tests

    /// <summary>
    /// Get Event Type Distribution
    /// </summary>
    [Test]
    public async Task GetEventTypeDistributionAsync_ReturnsDistribution()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Type.A",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Type.A",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Type.B",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetEventTypeDistributionAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.First(static x => x.EventType == "Type.A").Count, Is.EqualTo(2));
        Assert.That(result.First(static x => x.EventType == "Type.B").Count, Is.EqualTo(1));
    }

    /// <summary>
    /// Get Event Type Distribution ordered by count descending
    /// </summary>
    [Test]
    public async Task GetEventTypeDistributionAsync_OrdersByCountDescending()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Rare", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Common", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetEventTypeDistributionAsync();

        // Assert
        var resultList = result.ToList();
        Assert.That(resultList[0].EventType, Is.EqualTo("Common"));
        Assert.That(resultList[0].Count, Is.GreaterThan(resultList[1].Count));
    }

    /// <summary>
    /// Get Event Type Distribution with date range filters correctly
    /// </summary>
    [Test]
    public async Task GetEventTypeDistributionAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-5);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "InRange",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "OutOfRange",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetEventTypeDistributionAsync(startDate, endDate);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result.First().EventType, Is.EqualTo("InRange"));
    }

    #endregion

    #region GetTopUsersAsync Tests

    /// <summary>
    /// Get Top Users
    /// </summary>
    [Test]
    public async Task GetTopUsersAsync_ReturnsTopUsers()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "user1@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "user1@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "user2@test.com", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetTopUsersAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0].Count, Is.GreaterThan(result[1].Count));
    }

    /// <summary>
    /// Get Top Users limits to specified count
    /// </summary>
    [Test]
    public async Task GetTopUsersAsync_LimitsToCount()
    {
        // Arrange
        var events = Enumerable.Range(0, 20)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                User = $"user{i}@test.com",
                InsertedDate = DateTimeOffset.UtcNow
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetTopUsersAsync(5);

        // Assert
        Assert.That(result, Has.Count.EqualTo(5));
    }

    /// <summary>
    /// Get Top Users with date range filters correctly
    /// </summary>
    [Test]
    public async Task GetTopUsersAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-5);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = "recent@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "old@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GetTopUsersAsync(10, startDate, endDate);

        // Assert
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].User, Is.EqualTo("recent@test.com"));
    }

    #endregion

    #region GenerateAuditReportAsync Tests

    /// <summary>
    /// Generate Audit Report
    /// </summary>
    [Test]
    public async Task GenerateAuditReportAsync_GeneratesReport()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = "user1@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _reportService.GenerateAuditReportAsync(
            startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Length, Is.GreaterThan(0));

        var reportText = System.Text.Encoding.UTF8.GetString(result);
        Assert.That(reportText, Does.Contain("Audit Report"));
        Assert.That(reportText, Does.Contain(startDate.ToString("yyyy-MM-dd")));
    }

    #endregion
}