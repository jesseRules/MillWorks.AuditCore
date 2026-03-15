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
/// Unit tests for AuditSearchService
/// </summary>
[TestFixture]
public class AuditSearchServiceTests
{
    /// <summary>
    /// Context for in-memory database
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Mock AutoMapper
    /// </summary>
    private Mock<IMapper> _mockMapper;

    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<AuditSearchService>> _mockLogger;

    /// <summary>
    /// Service under test
    /// </summary>
    private AuditSearchService _searchService;

    /// <summary>
    /// Setup method to initialize test dependencies
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(options);
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditSearchService>>();

        _searchService = new AuditSearchService(
            _context,
            _mockMapper.Object,
            _mockLogger.Object);
    }

    /// <summary>
    /// Tear down method to clean up after tests
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region SearchAuditEventsAsync Tests

    /// <summary>
    /// SearchAuditEventsAsync with basic request returns results
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithBasicRequest_ReturnsResults()
    {
        // Arrange
        var events = CreateTestEvents(10);
        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            Offset = 0,
            Limit = 50
        };

        var eventDtos = events.Select(static e => new AuditEventDto
        {
            EventId = e.EventId,
            EventType = e.EventType
        }).ToList();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(eventDtos);

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(10));
        Assert.That(result.Items, Has.Count.EqualTo(10));
    }

    /// <summary>
    /// SearchAuditEventsAsync with date range filters correctly
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow.AddDays(-1);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5) // Within range
            },
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) // Before range
            },
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow // After range
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            StartDate = startDate,
            EndDate = endDate,
            Offset = 0,
            Limit = 50
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
    }

    /// <summary>
    /// SearchAuditEventsAsync with user filter filters correctly
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithUserFilter_FiltersCorrectly()
    {
        // Arrange
        var targetUser = "target@test.com";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = targetUser,
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "other@test.com",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            User = targetUser,
            Offset = 0,
            Limit = 50
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                User = e.User
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items?[0].User, Is.EqualTo(targetUser));
    }

    /// <summary>
    /// SearchAuditEventsAsync with event type filter filters correctly
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithEventTypeFilter_FiltersCorrectly()
    {
        // Arrange
        var targetEventType = "User.Login";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = targetEventType,
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            EventType = targetEventType,
            Offset = 0,
            Limit = 50
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                EventType = e.EventType
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items?[0].EventType, Is.EqualTo(targetEventType));
    }

    /// <summary>
    /// SearchAuditEventsAsync with search term searches multiple fields
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithSearchTerm_SearchesMultipleFields()
    {
        // Arrange
        var searchTerm = "important";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                JsonData = "{\"note\":\"important data\"}",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "important@test.com",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Important.Event",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = "ImportantEntity",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "other@test.com",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            SearchTerm = searchTerm,
            Offset = 0,
            Limit = 50
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(4)); // Should find in all 4 fields
    }

    /// <summary>
    /// SearchAuditEventsAsync with pagination returns correct page
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithPagination_ReturnsCorrectPage()
    {
        // Arrange
        var events = CreateTestEvents(100);
        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            Offset = 50,
            Limit = 20
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(100));
        Assert.That(result.Items?.Count, Is.EqualTo(20));
        Assert.That(result.CurrentPage, Is.EqualTo(3)); // (50/20) + 1
        Assert.That(result.TotalPages, Is.EqualTo(5)); // Ceiling(100/20)
    }

    /// <summary>
    /// SearchAuditEventsAsync enriches results with parsed JSON data
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_EnrichesWithParsedJsonData()
    {
        // Arrange
        var testEventId = Guid.NewGuid();
        var jsonData = $"{{\"EventId\":\"{testEventId}\",\"EventType\":\"test-type\"}}";

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                JsonData = jsonData,
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest
        {
            Offset = 0,
            Limit = 50
        };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                JsonData = e.JsonData
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.Items?[0].Data, Is.Not.Null);
        Assert.That(result.Items[0].Data?.EventId, Is.EqualTo(testEventId));
        Assert.That(result.Items[0].Data.EventType, Is.EqualTo("test-type"));
    }

    /// <summary>
    /// SearchAuditEventsAsync with malformed JSON creates error response
    /// </summary>
    [Test]
    public async Task SearchAuditEventsAsync_WithMalformedJson_CreatesErrorResponse()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                JsonData = "invalid json {{{",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var request = new AuditSearchRequest { Offset = 0, Limit = 50 };

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                JsonData = e.JsonData
            }).ToList());

        // Act
        var result = await _searchService.SearchAuditEventsAsync(request);

        // Assert
        Assert.That(result.Items?[0].Data, Is.Not.Null);
        Assert.That(result.Items[0].Data?.ErrorMessage, Does.Contain("Failed to parse"));
        Assert.That(result.Items[0].Data.CustomFields!["ParseError"], Is.True);
    }

    #endregion

    #region GetDistinctUsersAsync Tests

    /// <summary>
    /// GetDistinctUsersAsync returns unique users
    /// </summary>
    [Test]
    public async Task GetDistinctUsersAsync_ReturnsUniqueUsers()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "user1@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "user1@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "user2@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = null, InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctUsersAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("user1@test.com"));
        Assert.That(result, Does.Contain("user2@test.com"));
    }

    /// <summary>
    /// GetDistinctUsersAsync returns alphabetically sorted users
    /// </summary>
    [Test]
    public async Task GetDistinctUsersAsync_ReturnsAlphabeticallySorted()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "zebra@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "alpha@test.com", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), User = "middle@test.com", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctUsersAsync();

        // Assert
        Assert.That(result[0], Is.EqualTo("alpha@test.com"));
        Assert.That(result[1], Is.EqualTo("middle@test.com"));
        Assert.That(result[2], Is.EqualTo("zebra@test.com"));
    }

    #endregion

    #region GetDistinctEventTypesAsync Tests

    /// <summary>
    /// GetDistinctEventTypesAsync returns unique event types
    /// </summary>
    [Test]
    public async Task GetDistinctEventTypesAsync_ReturnsUniqueEventTypes()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Data.Update", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = null, InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctEventTypesAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("User.Login"));
        Assert.That(result, Does.Contain("Data.Update"));
    }

    /// <summary>
    /// GetDistinctEventTypesAsync returns alphabetically sorted event types
    /// </summary>
    [Test]
    public async Task GetDistinctEventTypesAsync_ReturnsAlphabeticallySorted()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Zebra.Event", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "Alpha.Event", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctEventTypesAsync();

        // Assert
        Assert.That(result[0], Is.EqualTo("Alpha.Event"));
        Assert.That(result[1], Is.EqualTo("Zebra.Event"));
    }

    #endregion

    #region GetDistinctEntityTypesAsync Tests

    /// <summary>
    /// GetDistinctEntityTypesAsync returns unique entity types
    /// </summary>
    [Test]
    public async Task GetDistinctEntityTypesAsync_ReturnsUniqueEntityTypes()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EntityType = "Customer", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "Customer", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "Order", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EntityType = "", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _searchService.GetDistinctEntityTypesAsync();

        // Assert
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result, Does.Contain("Customer"));
        Assert.That(result, Does.Contain("Order"));
    }

    #endregion

    #region SearchByEntityAsync Tests

    /// <summary>
    /// SearchByEntityAsync with only entity type returns all entities of that type
    /// </summary>
    [Test]
    public async Task SearchByEntityAsync_WithEntityTypeOnly_ReturnsAllEntitiesOfType()
    {
        // Arrange
        var entityType = "Product";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = "OtherType",
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                EventType = e.EntityType
            }).ToList());

        // Act
        var result = await _searchService.SearchByEntityAsync(entityType);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(2));
    }

    /// <summary>
    /// SearchByEntityAsync with entity type and ID filters to specific entity
    /// </summary>
    [Test]
    public async Task SearchByEntityAsync_WithEntityId_FiltersToSpecificEntity()
    {
        // Arrange
        var entityType = "Order";
        var entityId = Guid.NewGuid().ToString();

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = entityId,
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                EntityId = e.EntityId
            }).ToList());

        // Act
        var result = await _searchService.SearchByEntityAsync(entityType, entityId);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items?[0].EntityId, Is.EqualTo(entityId));
    }

    /// <summary>
    /// SearchByEntityAsync with pagination returns correct page
    /// </summary>
    [Test]
    public async Task SearchByEntityAsync_WithPagination_ReturnsCorrectPage()
    {
        // Arrange
        var entityType = "Customer";
        var events = CreateTestEventsWithEntityType(entityType, 50);

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId
            }).ToList());

        // Act
        var result = await _searchService.SearchByEntityAsync(
            entityType, offset: 20, limit: 10);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(50));
        Assert.That(result.Items?.Count, Is.EqualTo(10));
        Assert.That(result.CurrentPage, Is.EqualTo(3));
    }

    #endregion

    #region SearchByUserAsync Tests

    /// <summary>
    /// SearchByUserAsync with username returns user events
    /// </summary>
    [Test]
    public async Task SearchByUserAsync_WithUsername_ReturnsUserEvents()
    {
        // Arrange
        var username = "target@test.com";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = username,
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = "other@test.com",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId,
                User = e.User
            }).ToList());

        // Act
        var result = await _searchService.SearchByUserAsync(username);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
        Assert.That(result.Items?[0].User, Is.EqualTo(username));
    }

    /// <summary>
    /// SearchByUserAsync with date range filters correctly
    /// </summary>
    [Test]
    public async Task SearchByUserAsync_WithDateRange_FiltersCorrectly()
    {
        // Arrange
        var username = "user@test.com";
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow.AddDays(-1);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                User = username,
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-5) // Within range
            },
            new()
            {
                EventId = Guid.NewGuid(),
                User = username,
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) // Before range
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(static (List<AuditEventEntity> src) => src.Select(static e => new AuditEventDto
            {
                EventId = e.EventId
            }).ToList());

        // Act
        var result = await _searchService.SearchByUserAsync(
            username, startDate, endDate);

        // Assert
        Assert.That(result.TotalItems, Is.EqualTo(1));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a list of test audit events
    /// </summary>
    /// <param name="count"></param>
    /// <returns></returns>
    private List<AuditEventEntity> CreateTestEvents(int count)
    {
        return Enumerable.Range(0, count)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.Type{i % 5}",
                User = $"user{i % 10}@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();
    }

    /// <summary>
    /// Creates a list of test audit events with specified entity type
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="count"></param>
    /// <returns></returns>
    private List<AuditEventEntity> CreateTestEventsWithEntityType(string entityType, int count)
    {
        return Enumerable.Range(0, count)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EntityType = entityType,
                EntityId = Guid.NewGuid().ToString(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();
    }

    #endregion
}