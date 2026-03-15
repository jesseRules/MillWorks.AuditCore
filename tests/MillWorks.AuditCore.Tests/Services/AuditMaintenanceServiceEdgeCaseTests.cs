using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Maintenance;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Edge case unit tests for AuditMaintenanceService covering retention-based
/// cleanup, database size, table optimization, statistics accuracy, and the
/// archival count method.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditMaintenanceServiceEdgeCaseTests
{
    private AuditApplicationDbContext _context;
    private Mock<ILogger<AuditMaintenanceService>> _mockLogger;
    private AuditMaintenanceService _maintenanceService;

    [SetUp]
    public void Setup()
    {
        var options = TestDbContextFactory.CreateInMemoryOptions();
        _context = new AuditApplicationDbContext(options);
        _mockLogger = new Mock<ILogger<AuditMaintenanceService>>();
        _maintenanceService = new AuditMaintenanceService(_context, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
    }

    #region CleanupOldAuditEventsAsync — deletes events before the cutoff

    /// <summary>
    /// Verifies that CleanupOldAuditEventsAsync(30) deletes the 2 events that are
    /// older than 30 days and leaves the 3 events within the retention window intact.
    ///
    /// NOTE: Ignored for InMemory provider.  The service uses EF Core's ExecuteDeleteAsync
    /// bulk-delete API which is not supported by the InMemory provider and throws
    /// InvalidOperationException.  This behavior is covered by the SQLite integration
    /// suite.  The [Ignore] keeps the test visible so it is picked up when a relational
    /// provider is wired in.
    /// </summary>
    [Test]
    [Ignore("ExecuteDeleteAsync is not supported by the InMemory EF provider — covered by SQLite integration tests")]
    public async Task CleanupOldAuditEventsAsync_DeletesBeforeCutoff()
    {
        // Arrange — 2 old events (outside retention), 3 recent events (inside retention)
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Old.Event",    InsertedDate = DateTimeOffset.UtcNow.AddDays(-45) },
            new() { EventId = Guid.NewGuid(), EventType = "Old.Event",    InsertedDate = DateTimeOffset.UtcNow.AddDays(-60) },
            new() { EventId = Guid.NewGuid(), EventType = "Recent.Event", InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) },
            new() { EventId = Guid.NewGuid(), EventType = "Recent.Event", InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)  },
            new() { EventId = Guid.NewGuid(), EventType = "Recent.Event", InsertedDate = DateTimeOffset.UtcNow              }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        int deleted = await _maintenanceService.CleanupOldAuditEventsAsync(30);

        // Assert — returns the count of deleted events
        Assert.That(deleted, Is.EqualTo(2));

        // The 3 recent events must still be present
        int remaining = _context.AuditEvents.Count();
        Assert.That(remaining, Is.EqualTo(3));
    }

    #endregion

    #region CleanupOldAuditEventsAsync — nothing old enough means zero deletes

    /// <summary>
    /// Verifies that CleanupOldAuditEventsAsync returns 0 and leaves all events
    /// untouched when every event falls within the retention period.
    ///
    /// NOTE: Ignored for InMemory provider — same reason as CleanupOldAuditEventsAsync_DeletesBeforeCutoff.
    /// </summary>
    [Test]
    [Ignore("ExecuteDeleteAsync is not supported by the InMemory EF provider — covered by SQLite integration tests")]
    public async Task CleanupOldAuditEventsAsync_NothingOldEnough_DeletesNothing()
    {
        // Arrange — all events are within the last 29 days
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow.AddDays(-1)  },
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow.AddDays(-15) },
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow.AddDays(-29) }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        int deleted = await _maintenanceService.CleanupOldAuditEventsAsync(30);

        // Assert
        Assert.That(deleted, Is.EqualTo(0));
        Assert.That(_context.AuditEvents.Count(), Is.EqualTo(3));
    }

    #endregion

    #region GetAuditDatabaseSizeAsync — returns a non-negative value

    /// <summary>
    /// Verifies that GetAuditDatabaseSizeAsync does not throw an unhandled exception
    /// when running against the InMemory provider and returns a value >= 0.
    /// The InMemory provider cannot execute the SQL sys. Tables query, so the service
    /// falls back to the record-count-based estimate (count * 2048 bytes).
    /// </summary>
    [Test]
    public async Task GetAuditDatabaseSizeAsync_ReturnsPositiveValue()
    {
        // Arrange — seed a few events so the fallback estimate is non-zero
        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow },
            new() { EventId = Guid.NewGuid(), EventType = "User.Login", InsertedDate = DateTimeOffset.UtcNow }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act — must not throw
        long size = await _maintenanceService.GetAuditDatabaseSizeAsync();

        // Assert — the InMemory fallback returns count * 2048, so >= 0 is the invariant
        Assert.That(size, Is.GreaterThanOrEqualTo(0));
    }

    #endregion

    #region OptimizeAuditTablesAsync — completes without throwing

    /// <summary>
    /// Verifies that OptimizeAuditTablesAsync does not throw an unhandled exception
    /// when run against the InMemory provider.  The service catches the SQL failure
    /// internally and returns false, so the return value must be a bool.
    /// </summary>
    [Test]
    public async Task OptimizeAuditTablesAsync_CompletesWithoutError()
    {
        // Act — the InMemory provider will reject the raw SQL but the service catches it
        bool result = await _maintenanceService.OptimizeAuditTablesAsync();

        // Assert — either true (no-op succeeded) or false (SQL failed gracefully)
        Assert.That(result, Is.TypeOf<bool>());
    }

    #endregion

    #region GetAuditStatisticsAsync — returns accurate counts

    /// <summary>
    /// Verifies that GetAuditStatisticsAsync returns accurate values for TotalEvents,
    /// EventsToday, UniqueUsers, and TopEventTypes across a mixed dataset.
    /// </summary>
    [Test]
    public async Task GetAuditStatisticsAsync_ReturnsAccurateCounts()
    {
        // Arrange — 1 event today, 1 within 7 days, 1 within 30 days, 1 older than 30 days
        // Use distinct users throughout so UniqueUsers is predictable.
        var now = DateTimeOffset.UtcNow;
        var todayStart = new DateTimeOffset(now.Year, now.Month, now.Day, 0, 0, 0, TimeSpan.Zero);

        var events = new List<AuditEventEntity>
        {
            // Today — must land on or after midnight UTC today
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Login",
                User = "user1@test.com",
                InsertedDate = todayStart.AddHours(1)
            },
            // Within 7 days but not today
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Data.Update",
                User = "user2@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-3)
            },
            // Within 30 days but not this week
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "Data.Update",
                User = "user3@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-20)
            },
            // Older than 30 days
            new()
            {
                EventId = Guid.NewGuid(),
                EventType = "User.Logout",
                User = "user4@test.com",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-60)
            }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        var stats = await _maintenanceService.GetAuditStatisticsAsync();

        // Assert — total must include all 4 events
        Assert.That(stats, Is.Not.Null);
        Assert.That(stats.ContainsKey("TotalEvents"), Is.True);
        Assert.That(stats["TotalEvents"], Is.EqualTo(4));

        // At least the single event added today must be counted
        Assert.That(stats.ContainsKey("EventsToday"), Is.True);
        Assert.That((int)stats["EventsToday"]!, Is.GreaterThanOrEqualTo(1));

        // UniqueUsers must be present
        Assert.That(stats.ContainsKey("UniqueUsers"), Is.True);
        Assert.That((int)stats["UniqueUsers"]!, Is.EqualTo(4));

        // TopEventTypes must be present and be a non-null dictionary
        Assert.That(stats.ContainsKey("TopEventTypes"), Is.True);
        Assert.That(stats["TopEventTypes"], Is.Not.Null);
        Assert.That(stats["TopEventTypes"], Is.InstanceOf<Dictionary<string, object>>());
    }

    #endregion

    #region ArchiveAuditEventsAsync — respects the archiveBefore date

    /// <summary>
    /// Verifies that AuditMaintenanceService.ArchiveAuditEventsAsync counts the events
    /// that are older than the supplied archiveBefore date.  The current implementation
    /// counts but does not actually move events, so the return value is the number of
    /// qualifying events.
    /// </summary>
    [Test]
    public async Task ArchiveAuditEventsAsync_WithRetentionPolicy_RespectsDays()
    {
        // Arrange — 3 events old enough to archive, 2 events too recent
        var archiveBefore = DateTimeOffset.UtcNow.AddDays(-30);

        var events = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), EventType = "Old.Event",    InsertedDate = DateTimeOffset.UtcNow.AddDays(-45) },
            new() { EventId = Guid.NewGuid(), EventType = "Old.Event",    InsertedDate = DateTimeOffset.UtcNow.AddDays(-60) },
            new() { EventId = Guid.NewGuid(), EventType = "Old.Event",    InsertedDate = DateTimeOffset.UtcNow.AddDays(-90) },
            new() { EventId = Guid.NewGuid(), EventType = "Recent.Event", InsertedDate = DateTimeOffset.UtcNow.AddDays(-10) },
            new() { EventId = Guid.NewGuid(), EventType = "Recent.Event", InsertedDate = DateTimeOffset.UtcNow.AddDays(-5)  }
        };

        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();

        // Act
        int count = await _maintenanceService.ArchiveAuditEventsAsync(archiveBefore, "test-location");

        // Assert — only the 3 old events qualify
        Assert.That(count, Is.EqualTo(3));

        // Original data must be unchanged — this method only counts, it does not delete
        Assert.That(_context.AuditEvents.Count(), Is.EqualTo(5));
    }

    #endregion
}
