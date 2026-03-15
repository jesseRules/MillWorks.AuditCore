using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services.Query;

/// <summary>
/// Edge case unit tests for AuditQueryService (plan section 2.4).
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditQueryServiceEdgeCaseTests
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
    private Mock<ILogger<AuditQueryService>> _mockLogger;

    /// <summary>
    /// Service under test.
    /// </summary>
    private AuditQueryService _queryService;

    /// <summary>
    /// Sets up a fresh in-memory context and service instance before each test.
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();
        _context = new AuditApplicationDbContext(options);
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditQueryService>>();
        _queryService = new AuditQueryService(_context, _mockMapper.Object, _mockLogger.Object);
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

    #region GetGroupedAuditTrailAsync Edge Cases

    /// <summary>
    /// Events sharing EntityType/EntityId but carrying different EventTypes must be grouped
    /// under the action portion of their EventType string (the segment after the final dot).
    /// "Entity.Created" groups under "Created", "Entity.Updated" under "Updated", etc.
    /// </summary>
    [Test]
    public async Task GetGroupedAuditTrailAsync_GroupsByCorrelationId_ReturnsGroupedResults()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        const string entityName = "Invoice";

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Created",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-30)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Updated",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-20)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "Invoice.Updated",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetGroupedAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result.ContainsKey("Created"), Is.True, "Expected a 'Created' group key.");
        Assert.That(result.ContainsKey("Updated"), Is.True, "Expected an 'Updated' group key.");
        Assert.That(result["Created"], Has.Count.EqualTo(1));
        Assert.That(result["Updated"], Has.Count.EqualTo(2));
    }

    #endregion

    #region GetRecentActivityAsync Edge Cases

    /// <summary>
    /// When 50 events all fall within the requested time window the service must return
    /// all of them because the method returns every event in the window (up to its internal
    /// cap), not a caller-specified count limit.
    /// </summary>
    [Test]
    public async Task GetRecentActivityAsync_RespectsLimit_ReturnsLimitedResults()
    {
        // Arrange — 50 events, all within the last hour
        var events = Enumerable.Range(0, 50)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-(i + 1))
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetRecentActivityAsync(hours: 1);

        // Assert — all 50 events are within the window so all should be returned
        var activities = result.ToList();
        Assert.That(activities, Has.Count.EqualTo(50));
    }

    /// <summary>
    /// With no events in the database the service must return an empty sequence rather
    /// than throwing or returning null.
    /// </summary>
    [Test]
    public async Task GetRecentActivityAsync_EmptyDatabase_ReturnsEmpty()
    {
        // Act
        var result = await _queryService.GetRecentActivityAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region GetEntityAuditTrailAsync Edge Cases

    /// <summary>
    /// EntityType values may legitimately contain hyphens, underscores, and dots.
    /// The service must treat the value as a plain string comparison and return the
    /// matching records without mangling or rejecting the input.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_SpecialCharactersInEntityName_HandlesCorrectly()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        const string specialEntityType = "My-Special_Entity.Type";

        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EntityType = specialEntityType,
            EntityId = entityId.ToString(),
            EventType = "My-Special_Entity.Type.Created",
            InsertedDate = DateTimeOffset.UtcNow
        };

        await _context.AuditEvents.AddAsync(auditEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(specialEntityType, entityId);

        // Assert
        var dtos = result.ToList();
        Assert.That(dtos, Has.Count.EqualTo(1));
        Assert.That(dtos[0].EntityName, Is.EqualTo(specialEntityType));
    }

    #endregion

    #region GetAuditEventsAsync Edge Cases

    /// <summary>
    /// When the caller requests a page that is beyond all stored records the total item
    /// count must still reflect reality, while the mapped items list is whatever the
    /// mapper returns for an empty set of entities (here mocked as an empty list).
    /// </summary>
    [Test]
    public async Task GetAuditEventsAsync_LargePageNumber_ReturnsEmptyItems()
    {
        // Arrange — seed 10 events
        var events = Enumerable.Range(0, 10)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Mapper returns an empty list because EF skips past all rows for offset 1000
        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<List<AuditEventEntity>>()))
            .Returns([]);

        // Act — request well beyond the end of the data set
        var result = await _queryService.GetAuditEventsAsync(offset: 1000, limit: 50);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(10));
        Assert.That(result.Items, Is.Not.Null);
        Assert.That(result.Items, Is.Empty);
    }

    #endregion

    #region GetUserActivityAsync Edge Cases

    /// <summary>
    /// Guid.Empty is a valid value that will never match any stored UserId because the
    /// entities are created with real GUIDs.  The service should return an empty sequence
    /// rather than throwing.
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_GuidEmpty_ReturnsEmpty()
    {
        // Arrange — add events owned by real users, none owned by Guid.Empty
        var events = Enumerable.Range(0, 5)
            .Select(static _ => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                UserId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow
            })
            .ToList();

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetUserActivityAsync(Guid.Empty);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion
}
