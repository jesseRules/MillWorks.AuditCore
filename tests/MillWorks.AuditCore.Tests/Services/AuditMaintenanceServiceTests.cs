using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Maintenance;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// AuditMaintenanceService tests
/// </summary>
[TestFixture]
public class AuditMaintenanceServiceTests
{
    private AuditDbContext _context;
    private Mock<ILogger<AuditMaintenanceService>> _mockLogger;
    private AuditMaintenanceService _maintenanceService;

    /// <summary>
    /// Setup initializes before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(options);
        _mockLogger = new Mock<ILogger<AuditMaintenanceService>>();

        _maintenanceService = new AuditMaintenanceService(_context, _mockLogger.Object);
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
    /// GetAuditStatisticsAsync returns statistics
    /// </summary>
    [Test]
    public async Task GetAuditStatisticsAsync_ReturnsStatistics()
    {
        // Arrange
        var events = new List<AuditEventEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                User = "user1",
                InsertedDate = DateTimeOffset.UtcNow
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                User = "user2",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-1)
            }
        };

        _context.AuditEvents.AddRange(events);
        await _context.SaveChangesAsync();

        // Act
        var stats = await _maintenanceService.GetAuditStatisticsAsync();

        // Assert
        Assert.That(stats, Is.Not.Null);
        Assert.That(stats.ContainsKey("TotalEvents"), Is.True);
        Assert.That(stats["TotalEvents"], Is.EqualTo(2));
        Assert.That(stats.ContainsKey("UniqueUsers"), Is.True);
    }

}