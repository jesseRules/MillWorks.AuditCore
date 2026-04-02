using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Maintenance;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public class AuditMaintenanceServiceSqliteTests
{
    private SqliteConnection _connection = null!;
    private AuditApplicationDbContext _context = null!;
    private AuditMaintenanceService _service = null!;
    private Mock<ILogger<AuditMaintenanceService>> _mockLogger = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new AuditApplicationDbContext(options);
        _context.Database.EnsureCreated();

        _mockLogger = new Mock<ILogger<AuditMaintenanceService>>();
        _service = new AuditMaintenanceService(_context, _mockLogger.Object);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task CleanupOldAuditEventsAsync_DeletesOnlyEventsOlderThanRetention()
    {
        await SeedEventsAsync(
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-60), "Old.A"),
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-31), "Old.B"),
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-29), "Recent.A"),
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-1), "Recent.B"));

        var deleted = await _service.CleanupOldAuditEventsAsync(30);

        Assert.That(deleted, Is.EqualTo(2));
        Assert.That(await _context.AuditEvents.CountAsync(), Is.EqualTo(2));
        var remainingTypes = await _context.AuditEvents.Select(static x => x.EventType).ToListAsync();
        Assert.That(remainingTypes, Is.EquivalentTo(new[] { "Recent.A", "Recent.B" }));
    }

    [Test]
    public async Task CleanupOldAuditEventsAsync_WhenNothingEligible_ReturnsZeroAndPreservesRows()
    {
        await SeedEventsAsync(
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-10), "Recent.A"),
            CreateEvent(DateTimeOffset.UtcNow.AddDays(-2), "Recent.B"));

        var deleted = await _service.CleanupOldAuditEventsAsync(30);

        Assert.That(deleted, Is.EqualTo(0));
        Assert.That(await _context.AuditEvents.CountAsync(), Is.EqualTo(2));
    }

    [Test]
    public async Task CleanupOldAuditEventsAsync_LargeDataset_DeletesAcrossMultipleBatches()
    {
        var oldEvents = Enumerable.Range(1, 1505)
            .Select(i => CreateEvent(DateTimeOffset.UtcNow.AddDays(-90), $"Old.{i}"));
        var recentEvents = Enumerable.Range(1, 5)
            .Select(i => CreateEvent(DateTimeOffset.UtcNow.AddDays(-1), $"Recent.{i}"));

        await SeedEventsAsync(oldEvents.Concat(recentEvents).ToArray());

        var deleted = await _service.CleanupOldAuditEventsAsync(30);

        Assert.That(deleted, Is.EqualTo(1505));
        Assert.That(await _context.AuditEvents.CountAsync(), Is.EqualTo(5));
    }

    [Test]
    public void CleanupOldAuditEventsAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () =>
            await _service.CleanupOldAuditEventsAsync(30, cts.Token));
    }

    private async Task SeedEventsAsync(params AuditEventEntity[] events)
    {
        await _context.AuditEvents.AddRangeAsync(events);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
    }

    private static AuditEventEntity CreateEvent(DateTimeOffset insertedDate, string eventType) =>
        new()
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            InsertedDate = insertedDate,
            JsonData = "{}"
        };
}
