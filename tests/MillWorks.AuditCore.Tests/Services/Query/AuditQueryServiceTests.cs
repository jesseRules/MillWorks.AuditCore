using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

/// <summary>
/// Unit tests for AuditQueryService
/// </summary>
[TestFixture]
public class AuditQueryServiceTests
{
    /// <summary>
    /// Context
    /// </summary>
    private AuditDbContext _context;

    /// <summary>
    /// Mock Mapper
    /// </summary>
    private Mock<IMapper> _mockMapper;

    /// <summary>
    /// Mock Logger
    /// </summary>
    private Mock<ILogger<AuditQueryService>> _mockLogger;

    /// <summary>
    /// Query Service
    /// </summary>
    private AuditQueryService _queryService;

    /// <summary>
    /// Set up
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(options);
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditQueryService>>();

        _queryService = new AuditQueryService(
            _context,
            _mockMapper.Object,
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

    #region GetEntityAuditTrailAsync Tests

    /// <summary>
    /// Get Entity
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_WithValidEntityId_ReturnsAuditTrail()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Customer";
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Customer.Created",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-2)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Customer.Updated",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-1)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos, Has.Count.EqualTo(2));
        Assert.That(auditLogDtos.Select(static x => x.EntityName), Has.All.EqualTo(entityName));
    }

    /// <summary>
    /// Get Entity
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_OrdersByDateDescending_ReturnsInCorrectOrder()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Order";
        var oldestDate = DateTimeOffset.UtcNow.AddDays(-3);
        var middleDate = DateTimeOffset.UtcNow.AddDays(-2);
        var newestDate = DateTimeOffset.UtcNow.AddDays(-1);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                InsertedDate = middleDate
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                InsertedDate = newestDate
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                InsertedDate = oldestDate
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos, Has.Count.EqualTo(3));
        Assert.That(auditLogDtos[0].CreatedAt, Is.GreaterThanOrEqualTo(auditLogDtos[1].CreatedAt));
        Assert.That(auditLogDtos[1].CreatedAt, Is.GreaterThanOrEqualTo(auditLogDtos[2].CreatedAt));
    }

    /// <summary>
    /// Get Entity
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_WithNonExistentEntity_ReturnsEmptyList()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "NonExistent";

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Empty);
    }

    /// <summary>
    /// Get Entity
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_WithJsonData_ParsesSuccessfully()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Product";
        var jsonData = "{\"OldPrice\":100,\"NewPrice\":150}";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = entityName,
            EntityId = entityId.ToString(),
            JsonData = jsonData,
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos, Has.Count.EqualTo(1));
        Assert.That(auditLogDtos[0].AdditionalData, Is.Not.Null);
    }

    #endregion

    #region GetUserActivityAsync Tests

    /// <summary>
    /// Get User Activity
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_WithValidUserId_ReturnsUserActivity()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow.AddHours(-1)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                EventType = "User.UpdateProfile",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetUserActivityAsync(userId);

        // Assert
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(2));
        Assert.That(activities.Select(static x => x.CreatedById), Has.All.EqualTo(userId));
    }

    /// <summary>
    /// Get User Activity
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_WithFromDate_FiltersCorrectly()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var fromDate = DateTimeOffset.UtcNow.AddDays(-1);

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-2) // Before fromDate
            },
            new()
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                InsertedDate = DateTimeOffset.UtcNow // After fromDate
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetUserActivityAsync(userId, fromDate);

        // Assert
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(1));
        Assert.That(activities[0].CreatedAt, Is.GreaterThanOrEqualTo(fromDate));
    }

    /// <summary>
    /// Get User Activity
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_WithTakeParameter_LimitsResults()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var events = Enumerable.Range(0, 100)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                UserId = userId,
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetUserActivityAsync(userId, take: 25);

        // Assert
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(25));
    }

    /// <summary>
    /// Get User Activity
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_WithNonExistentUser_ReturnsEmptyList()
    {
        // Arrange
        var userId = Guid.NewGuid();

        // Act
        var result = await _queryService.GetUserActivityAsync(userId);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetAuditEventsAsync Tests

    /// <summary>
    /// Get Audit Events
    /// </summary>
    [Test]
    public async Task GetAuditEventsAsync_WithValidPagination_ReturnsPaginatedResults()
    {
        // Arrange
        var events = Enumerable.Range(0, 100)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.Type{i}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        var eventDtos = events.Select(static e => new AuditEventDto
        {
            EventId = e.EventId,
            EventType = e.EventType
        }).ToList();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns(eventDtos.Take(50).ToList());

        // Act
        var result = await _queryService.GetAuditEventsAsync(offset: 0, limit: 50);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(100));
        Assert.That(result.Items?.Count, Is.EqualTo(50));
        Assert.That(result.Offset, Is.EqualTo(0));
        Assert.That(result.Limit, Is.EqualTo(50));
        Assert.That(result.TotalPages, Is.EqualTo(2));
        Assert.That(result.CurrentPage, Is.EqualTo(1));
    }

    /// <summary>
    /// Get Audit Events
    /// </summary>
    [Test]
    public async Task GetAuditEventsAsync_WithSecondPage_ReturnsCorrectPage()
    {
        // Arrange
        var events = Enumerable.Range(0, 100)
            .Select(static i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns([]);

        // Act
        var result = await _queryService.GetAuditEventsAsync(offset: 50, limit: 50);

        // Assert
        Assert.That(result.CurrentPage, Is.EqualTo(2));
        Assert.That(result.Offset, Is.EqualTo(50));
    }

    /// <summary>
    /// Get Audit Events
    /// </summary>
    [Test]
    public async Task GetAuditEventsAsync_ParsesJsonDataCorrectly()
    {
        // Arrange
        var eventDto = new AuditEventDto
        {
            EventId = Guid.NewGuid(),
            JsonData = "{\"Key\":\"Value\"}"
        };

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = eventDto.EventId.Value,
                JsonData = eventDto.JsonData,
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns([eventDto]);

        // Act
        var result = await _queryService.GetAuditEventsAsync();

        // Assert
        Assert.That(result.Items, Has.Count.EqualTo(1));
        Assert.That(result.Items[0].Data, Is.Not.Null);
    }

    /// <summary>
    /// Get Audit Events
    /// </summary>
    [Test]
    public async Task GetAuditEventsAsync_WithMalformedJson_LogsWarning()
    {
        // Arrange
        var eventDto = new AuditEventDto
        {
            EventId = Guid.NewGuid(),
            JsonData = "invalid json"
        };

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = eventDto.EventId.Value,
                JsonData = eventDto.JsonData,
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns([eventDto]);

        // Act
        var unused = await _queryService.GetAuditEventsAsync();

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Failed to parse JSON")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    #endregion

    #region GetAuditEventByIdAsync Tests

    /// <summary>
    /// Get Audit Event By Id
    /// </summary>
    [Test]
    public async Task GetAuditEventByIdAsync_WithValidId_ReturnsEvent()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        var eventDto = new AuditEventDto
        {
            EventId = eventId,
            EventType = "Test.Event"
        };

        _mockMapper
            .Setup(static x => x.Map<AuditEventDto>(It.IsAny<AuditEventEntity>()))
            .Returns(eventDto);

        // Act
        var result = await _queryService.GetAuditEventByIdAsync(eventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result?.EventId, Is.EqualTo(eventId));
    }

    /// <summary>
    /// Get Audit Event By Id
    /// </summary>
    [Test]
    public async Task GetAuditEventByIdAsync_WithNonExistentId_ReturnsNull()
    {
        // Arrange
        var eventId = Guid.NewGuid();

        // Act
        var result = await _queryService.GetAuditEventByIdAsync(eventId);

        // Assert
        Assert.That(result, Is.Null);
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("not found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Get Audit Event By Id
    /// </summary>
    [Test]
    public async Task GetAuditEventByIdAsync_WithJsonData_ParsesData()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var jsonData = "{\"Field\":\"Value\"}";

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            JsonData = jsonData,
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        var eventDto = new AuditEventDto
        {
            EventId = eventId,
            JsonData = jsonData
        };

        _mockMapper
            .Setup(static x => x.Map<AuditEventDto>(It.IsAny<AuditEventEntity>()))
            .Returns(eventDto);

        // Act
        var result = await _queryService.GetAuditEventByIdAsync(eventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Data, Is.Not.Null);
    }

    #endregion

    #region GetRecentActivityAsync Tests

    /// <summary>
    /// Get Recent Activity
    /// </summary>
    [Test]
    public async Task GetRecentActivityAsync_WithDefaultHours_ReturnsLast24Hours()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddHours(-23) // Within 24 hours
            },
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddHours(-25) // Outside 24 hours
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetRecentActivityAsync();

        // Assert
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// Get Recent Activity
    /// </summary>
    [Test]
    public async Task GetRecentActivityAsync_WithCustomHours_FiltersCorrectly()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddHours(-47) // Within 48 hours
            },
            new()
            {
                EventId = Guid.NewGuid(),
                InsertedDate = now.AddHours(-49) // Outside 48 hours
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetRecentActivityAsync(hours: 48);

        // Assert
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetGroupedAuditTrailAsync Tests

    /// <summary>
    /// Get Grouped Audit Trail
    /// </summary>
    [Test]
    public async Task GetGroupedAuditTrailAsync_GroupsByAction_ReturnsGroupedResults()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Invoice";

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Created",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Updated",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Updated",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetGroupedAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Not.Empty);
        Assert.That(result.ContainsKey("Created"), Is.True);
        Assert.That(result.ContainsKey("Updated"), Is.True);
        Assert.That(result["Updated"], Has.Count.EqualTo(2));
    }

    /// <summary>
    /// Get Grouped Audit Trail
    /// </summary>
    [Test]
    public async Task GetGroupedAuditTrailAsync_WithNoEvents_ReturnsEmptyDictionary()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "NonExistent";

        // Act
        var result = await _queryService.GetGroupedAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region MapEventTypeToAction Tests

    /// <summary>
    /// Map Event Type To Action
    /// </summary>
    [Test]
    public async Task MapEventTypeToAction_WithCreatedEvent_ReturnsCreatedAction()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Product";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = entityName,
            EntityId = entityId.ToString(),
            EventType = "Product.Created",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos[0].Action, Is.EqualTo(AuditAction.Created));
    }

    /// <summary>
    /// Map Event Type To Action
    /// </summary>
    [Test]
    public async Task MapEventTypeToAction_WithUpdatedEvent_ReturnsUpdatedAction()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Product";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = entityName,
            EntityId = entityId.ToString(),
            EventType = "Product.Updated",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos[0].Action, Is.EqualTo(AuditAction.Updated));
    }

    /// <summary>
    /// Map Event Type To Action
    /// </summary>
    [Test]
    public async Task MapEventTypeToAction_WithDeletedEvent_ReturnsDeletedAction()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Product";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = entityName,
            EntityId = entityId.ToString(),
            EventType = "Product.Deleted",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos[0].Action, Is.EqualTo(AuditAction.Deleted));
    }

    /// <summary>
    /// Map Event Type To Action
    /// </summary>
    [Test]
    public async Task MapEventTypeToAction_WithUnknownEvent_ReturnsUnknownAction()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "Product";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = entityName,
            EntityId = entityId.ToString(),
            EventType = "Product.CustomAction",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        var auditLogDtos = result.ToList();
        Assert.That(auditLogDtos[0].Action, Is.EqualTo(AuditAction.Unknown));
    }

    #endregion
}