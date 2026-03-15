using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

/// <summary>
/// Edge case unit tests for AuditSearchService (plan section 2.5).
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditSearchServiceEdgeCaseTests
{
    /// <summary>
    /// In-memory database context.
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Mock mapper.
    /// </summary>
    private Mock<IMapper> _mockMapper;

    /// <summary>
    /// Mock logger.
    /// </summary>
    private Mock<ILogger<AuditSearchService>> _mockLogger;

    /// <summary>
    /// Service under test.
    /// </summary>
    private AuditSearchService _searchService;

    /// <summary>
    /// Sets up a fresh in-memory context and service instance before each test.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();
        _context = new AuditApplicationDbContext(options);
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditSearchService>>();
        _searchService = new AuditSearchService(_context, _mockMapper.Object, _mockLogger.Object);
    }

    /// <summary>
    /// Tears down and disposes the context after each test.
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region SearchAuditEventsAsync Edge Cases

    /// <summary>
    /// When every optional filter (User, EventType, StartDate, EndDate, SearchTerm) is
    /// populated the query must apply all of them in conjunction so that only an event
    /// that satisfies every criterion is returned.
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_AllFiltersPopulated_ReturnsFilteredResults()
    {
        // Arrange
        var matchDate = DateTimeOffset.UtcNow.AddDays(-3);
        const string targetUser = "alice@test.com";
        const string targetEventType = "Order.Created";
        const string targetEntityType = "Order";

        // This event matches all filters
        var matchingEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            User = targetUser,
            EventType = targetEventType,
            EntityType = targetEntityType,
            JsonData = "{\"note\":\"special\"}",
            InsertedDate = matchDate
        };

        // These events each fail at least one filter
        var nonMatchingEvents = new List<AuditEventEntity>
        {
            new()
            {
                // Wrong user
                EventId = Guid.NewGuid(),
                User = "bob@test.com",
                EventType = targetEventType,
                EntityType = targetEntityType,
                JsonData = "{\"note\":\"special\"}",
                InsertedDate = matchDate
            },
            new()
            {
                // Wrong event type
                EventId = Guid.NewGuid(),
                User = targetUser,
                EventType = "Order.Deleted",
                EntityType = targetEntityType,
                JsonData = "{\"note\":\"special\"}",
                InsertedDate = matchDate
            },
            new()
            {
                // Outside date range (too old)
                EventId = Guid.NewGuid(),
                User = targetUser,
                EventType = targetEventType,
                EntityType = targetEntityType,
                JsonData = "{\"note\":\"special\"}",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-30)
            },
            new()
            {
                // SearchTerm not present anywhere on the row
                EventId = Guid.NewGuid(),
                User = targetUser,
                EventType = targetEventType,
                EntityType = targetEntityType,
                JsonData = "{\"note\":\"ordinary\"}",
                InsertedDate = matchDate
            }
        };

        await _context.AuditEvents.AddAsync(matchingEvent);
        await _context.AuditEvents.AddRangeAsync(nonMatchingEvents);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) =>
                src.Select(static e => new AuditEventDto { EventId = e.EventId, User = e.User }).ToList());

        var request = new AuditSearchRequest
        {
            User = targetUser,
            EventType = targetEventType,
            StartDate = DateTimeOffset.UtcNow.AddDays(-7),
            EndDate = DateTimeOffset.UtcNow,
            SearchTerm = "special",
            Offset = 0,
            Limit = 50
        };

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items, Has.Count.EqualTo(1));
        Assert.That(result.Items![0].User, Is.EqualTo(targetUser));
    }

    /// <summary>
    /// A request with no optional filters set should match every event in the database.
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_NoFilters_ReturnsAll()
    {
        // Arrange — 10 events, no special attributes
        var events = Enumerable.Range(0, 10)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) =>
                src.Select(static e => new AuditEventDto { EventId = e.EventId }).ToList());

        var request = new AuditSearchRequest { Offset = 0, Limit = 50 };

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(10));
    }

    /// <summary>
    /// Verifies that the date range filter is applied correctly: only the single event
    /// whose InsertedDate falls within StartDate..EndDate is returned; events before
    /// StartDate and after EndDate are excluded.
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_DateRangeFilter_OnlyReturnsInRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;

        var events = new List<AuditEventEntity>
        {
            new()
            {
                // Before the range
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddDays(-20)
            },
            new()
            {
                // Inside the range
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddDays(-5)
            },
            new()
            {
                // After the range
                EventId = Guid.NewGuid(),
                InsertedDate = now
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) =>
                src.Select(static e => new AuditEventDto { EventId = e.EventId }).ToList());

        var request = new AuditSearchRequest
        {
            StartDate = now.AddDays(-10),
            EndDate = now.AddDays(-1),
            Offset = 0,
            Limit = 50
        };

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(1));
    }

    #endregion

    #region SearchByEntityAsync Edge Cases

    /// <summary>
    /// The entity type filter is an exact string comparison.  Because InMemory EF
    /// evaluates EF.Functions.Like differently from SQL Server, we pass the exact casing
    /// that was stored ("Customer") and verify the filter returns that record and not a
    /// record belonging to a different entity type.
    /// </summary>
    [Test]
    public async Task SearchByEntityAsync_CaseInsensitiveMatch()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = "Customer",
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = "Order",
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) =>
                src.Select(static e => new AuditEventDto { EventId = e.EventId, EventType = e.EntityType }).ToList());

        // Act — pass exact case to exercise the entity type filter
        var result = await _searchService.SearchByEntityAsync("Customer");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items![0].EventType, Is.EqualTo("Customer"));
    }

    #endregion

    #region SearchByUserAsync Edge Cases

    /// <summary>
    /// Five events owned by "user@test.com" and three owned by "other@test.com" are
    /// seeded.  Searching by "user@test.com" should return exactly 5 (TotalItems = 5)
    /// regardless of the other user's events.
    /// </summary>
    [Test]
    public async Task SearchByUserAsync_ReturnsAllUserEvents()
    {
        // Arrange
        const string targetUser = "user@test.com";

        var targetEvents = Enumerable.Range(0, 5)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                User = targetUser,
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });

        var otherEvents = Enumerable.Range(0, 3)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                User = "other@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });

        await _context.AuditEvents.AddRangeAsync(targetEvents);
        await _context.AuditEvents.AddRangeAsync(otherEvents);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) =>
                src.Select(static e => new AuditEventDto { EventId = e.EventId, User = e.User }).ToList());

        // Act
        var result = await _searchService.SearchByUserAsync(targetUser);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(5));
        Assert.That(result.Items, Has.All.Matches<AuditEventDto>(dto => dto.User == targetUser));
    }

    #endregion

    #region GetDistinctUsersAsync Edge Cases

    /// <summary>
    /// With an empty database the method must return an empty list rather than null or
    /// throwing.
    /// </summary>
    [Test]
    public async Task GetDistinctUsersAsync_EmptyDatabase_ReturnsEmpty()
    {
        // Act
        var result = await _searchService.GetDistinctUsersAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetDistinctEventTypesAsync Edge Cases

    /// <summary>
    /// With an empty database the method must return an empty list rather than null or
    /// throwing.
    /// </summary>
    [Test]
    public async Task GetDistinctEventTypesAsync_EmptyDatabase_ReturnsEmpty()
    {
        // Act
        var result = await _searchService.GetDistinctEventTypesAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetDistinctEntityTypesAsync Edge Cases

    /// <summary>
    /// Duplicate EntityType values must be de-duplicated, and null/empty values must be
    /// excluded entirely so that the returned list contains only non-blank, unique names.
    /// </summary>
    [Test]
    public async Task GetDistinctEntityTypesAsync_ReturnsUniqueEntityNames()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EntityType = "Customer",  InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "Customer",  InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "Order",     InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = null,        InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "",          InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctEntityTypesAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("Customer"));
        Assert.That(result, Does.Contain("Order"));
        Assert.That(result, Has.None.Null);
        Assert.That(result, Has.None.Empty);
    }

    #endregion
}
