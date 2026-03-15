using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Query;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// AuditQueryService tests
/// </summary>
[TestFixture]
public class AuditQueryServiceTests
{
    private AuditApplicationDbContext _context;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<AuditQueryService>> _mockLogger;
    private AuditQueryService _queryService;

    /// <summary>
    /// Setup initializes before each test
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
    /// TearDown cleans up after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    /// <summary>
    /// GetEntityAuditTrailAsync returns audit logs for entity
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_ReturnsAuditLogs()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var entityName = "User";

        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EntityType = entityName,
                EntityId = entityId.ToString(),
                EventType = "User.Created",
                InsertedDate = DateTimeOffset.UtcNow
            }
        };

        _context.AuditEvents.AddRange(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        IEnumerable<AuditLogDto> auditLogDtos = result.ToList();
        Assert.That(auditLogDtos, Is.Not.Null);
        var resultList = auditLogDtos.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1));
    }

    /// <summary>
    /// GetRecentActivityAsync returns recent events
    /// </summary>
    [Test]
    public async Task GetRecentActivityAsync_ReturnsRecentEvents()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                InsertedDate = DateTimeOffset.UtcNow.AddHours(-5)
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-2)
            }
        };

        _context.AuditEvents.AddRange(events);
        await _context.SaveChangesAsync();

        // Act
        var result = await _queryService.GetRecentActivityAsync();

        // Assert
        IEnumerable<AuditLogDto> auditLogDtos = result.ToList();
        Assert.That(auditLogDtos, Is.Not.Null);
        var resultList = auditLogDtos.ToList();
        Assert.That(resultList, Has.Count.EqualTo(1)); // Only the event from 5 hours ago
    }
}