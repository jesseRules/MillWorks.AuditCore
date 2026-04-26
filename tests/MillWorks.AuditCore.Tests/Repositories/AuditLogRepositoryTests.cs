using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for AuditLogRepository query methods.
/// </summary>
[TestFixture]
public class AuditLogRepositoryTests
{
    private DbContextOptions<AuditDbContext> _options;
    private AuditDbContext _context;
    private AuditLogRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(_options);
        _repository = new AuditLogRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region GetEntityAuditTrailAsync

    /// <summary>
    /// Verifies retrieval of audit trail for a specific entity.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_ReturnsMatchingLogs()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var otherEntityId = Guid.NewGuid();

        await _context.AuditLogs.AddRangeAsync(
            CreateAuditLog("Order", entityId, AuditAction.Created),
            CreateAuditLog("Order", entityId, AuditAction.Updated),
            CreateAuditLog("Order", otherEntityId, AuditAction.Created),
            CreateAuditLog("Product", entityId, AuditAction.Created));
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetEntityAuditTrailAsync("Order", entityId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static r => r.EntityName == "Order"), Is.True);
        Assert.That(results.All(r => r.EntityId == entityId), Is.True);
    }

    /// <summary>
    /// Verifies audit trail is ordered by CreatedAt descending.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_OrderedByCreatedAtDescending()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        var older = CreateAuditLog("Order", entityId, AuditAction.Created);
        older.CreatedAt = DateTimeOffset.UtcNow.AddHours(-2);
        var newer = CreateAuditLog("Order", entityId, AuditAction.Updated);
        newer.CreatedAt = DateTimeOffset.UtcNow;

        await _context.AuditLogs.AddRangeAsync(older, newer);
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetEntityAuditTrailAsync("Order", entityId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].CreatedAt, Is.GreaterThan(results[1].CreatedAt));
    }

    /// <summary>
    /// Verifies empty result for non-existent entity.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_NoMatches_ReturnsEmpty()
    {
        // Act
        var results = (await _repository.GetEntityAuditTrailAsync("NonExistent", Guid.NewGuid())).ToList();

        // Assert
        Assert.That(results, Is.Empty);
    }

    #endregion

    #region GetUserActivityAsync

    /// <summary>
    /// Verifies retrieval of user activity logs.
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_ReturnsUserLogs()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();

        var log1 = CreateAuditLog("Order", Guid.NewGuid(), AuditAction.Created);
        log1.CreatedById = userId;
        var log2 = CreateAuditLog("Product", Guid.NewGuid(), AuditAction.Updated);
        log2.CreatedById = userId;
        var log3 = CreateAuditLog("Order", Guid.NewGuid(), AuditAction.Deleted);
        log3.CreatedById = otherUserId;

        await _context.AuditLogs.AddRangeAsync(log1, log2, log3);
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetUserActivityAsync(userId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(r => r.CreatedById == userId), Is.True);
    }

    /// <summary>
    /// Verifies fromDate filter works correctly.
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_WithFromDate_FiltersOlderRecords()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var old = CreateAuditLog("Order", Guid.NewGuid(), AuditAction.Created);
        old.CreatedById = userId;
        old.CreatedAt = now.AddDays(-10);

        var recent = CreateAuditLog("Order", Guid.NewGuid(), AuditAction.Updated);
        recent.CreatedById = userId;
        recent.CreatedAt = now.AddDays(-1);

        await _context.AuditLogs.AddRangeAsync(old, recent);
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetUserActivityAsync(userId, now.AddDays(-5))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].Action, Is.EqualTo(AuditAction.Updated));
    }

    /// <summary>
    /// Verifies the 100-record limit is applied.
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_LimitsTo100Records()
    {
        // Arrange
        var userId = Guid.NewGuid();
        for (int i = 0; i < 120; i++)
        {
            var log = CreateAuditLog("Entity", Guid.NewGuid(), AuditAction.Created);
            log.CreatedById = userId;
            log.CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i);
            await _context.AuditLogs.AddAsync(log);
        }
        await _context.SaveChangesAsync();

        // Act
        var results = (await _repository.GetUserActivityAsync(userId)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(100));
    }

    #endregion

    #region Helpers

    private static AuditLogEntity CreateAuditLog(string entityName, Guid entityId, AuditAction action)
    {
        return new AuditLogEntity
        {
            EntityName = entityName,
            EntityId = entityId,
            Action = action,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    #endregion
}
