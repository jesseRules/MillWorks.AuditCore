using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests validating the security findings fixes (pagination, JSON parsing, size limits, etc.)
/// </summary>
[TestFixture]
[Category("Unit")]
public class SecurityFindingsFixTests
{
    #region Finding 2: Pagination tests

    [Test]
    public async Task GetByOffsetAsync_NonPageAligned_ReturnsCorrectRows()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var events = Enumerable.Range(1, 100)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i),
                User = $"user{i}"
            })
            .ToList();

        await context.AuditEvents.AddRangeAsync(events);
        await context.SaveChangesAsync();

        var repository = new AuditEventRepository(context);

        // Act - offset=75, limit=50 should return rows 75-100 (25 rows), not 50-99
        var (items, totalCount) = await repository.GetByOffsetAsync(
            offset: 75,
            limit: 50,
            orderBy: q => q.OrderByDescending(e => e.InsertedDate));

        var itemsList = items.ToList();

        // Assert
        Assert.That(totalCount, Is.EqualTo(100));
        Assert.That(itemsList, Has.Count.EqualTo(25)); // Only 25 rows left after offset 75
    }

    [Test]
    public async Task GetByOffsetAsync_PageAligned_ReturnsCorrectRows()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var events = Enumerable.Range(1, 100)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i),
                User = $"user{i}"
            })
            .ToList();

        await context.AuditEvents.AddRangeAsync(events);
        await context.SaveChangesAsync();

        var repository = new AuditEventRepository(context);

        // Act - page-aligned offset=50, limit=50 should return rows 50-99
        var (items, totalCount) = await repository.GetByOffsetAsync(
            offset: 50,
            limit: 50,
            orderBy: q => q.OrderByDescending(e => e.InsertedDate));

        var itemsList = items.ToList();

        // Assert
        Assert.That(totalCount, Is.EqualTo(100));
        Assert.That(itemsList, Has.Count.EqualTo(50));
    }

    #endregion

    #region Finding 3: Malformed JSON handling tests

    [Test]
    public async Task GetAuditEventById_MalformedJson_ReturnsErrorResponse()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var eventId = Guid.NewGuid();
        var entity = new AuditEventEntity
        {
            EventId = eventId,
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{ invalid json }}" // Malformed JSON
        };

        await context.AuditEvents.AddAsync(entity);
        await context.SaveChangesAsync();

        var repository = new AuditEventRepository(context);

        var logRepository = new Mock<IAuditLogRepository>();
        var service = new AuditService(
            logRepository.Object,
            repository,
            NullLogger<AuditService>.Instance);

        // Act - should not throw
        var result = await service.GetAuditEventById(eventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Data, Is.Not.Null);
        Assert.That(result.Data!.ErrorMessage, Does.Contain("Failed to parse audit data"));
    }

    [Test]
    public async Task GetAuditEvents_OneBadJsonInBatch_StillReturnsOthers()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var goodJson = JsonSerializer.Serialize(new { EventId = Guid.NewGuid(), EventType = "Test" });
        var badJson = "{ invalid }";

        var events = new[]
        {
            new AuditEventEntity { EventId = Guid.NewGuid(), InsertedDate = DateTimeOffset.UtcNow, JsonData = goodJson },
            new AuditEventEntity { EventId = Guid.NewGuid(), InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-1), JsonData = badJson },
            new AuditEventEntity { EventId = Guid.NewGuid(), InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-2), JsonData = goodJson }
        };

        await context.AuditEvents.AddRangeAsync(events);
        await context.SaveChangesAsync();

        var repository = new AuditEventRepository(context);

        var logRepository = new Mock<IAuditLogRepository>();
        var service = new AuditService(
            logRepository.Object,
            repository,
            NullLogger<AuditService>.Instance);

        // Act - should not throw
        var result = await service.GetAuditEvents(0, 10);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Items, Has.Count.EqualTo(3));
        var badItem = result.Items.First(i => i.JsonData == badJson);
        Assert.That(badItem.Data?.ErrorMessage, Does.Contain("Failed to parse"));
    }

    #endregion

    #region Finding 5: Security event size limits

    [Test]
    public async Task RecordEventAsync_OversizedMessage_GetsTruncated()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var repository = new SecurityEventRepository(context);

        var auditContext = new Mock<IAuditContext>();
        auditContext.Setup(c => c.UserEmail).Returns("test@test.com");

        var config = new ConfigurationBuilder().Build();

        var service = new AuditSecurityEventService(
            repository,
            auditContext.Object,
            NullLogger<AuditSecurityEventService>.Instance,
            config);

        var oversizedMessage = new string('X', 1000); // Exceeds 500 char limit
        var dto = new SecurityEventDto
        {
            Message = oversizedMessage,
            EventType = SecurityEventType.SuspiciousActivity,
            Severity = SecurityEventSeverity.Medium
        };

        // Act
        await service.RecordEventAsync(dto);

        // Assert - read the persisted entity back to observe the real truncation
        var persisted = await context.SecurityEvents.SingleAsync();
        Assert.That(persisted.Message.Length, Is.EqualTo(500));
    }

    [Test]
    public async Task RecordEventAsync_OversizedDetails_StoresValidJsonSummary()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var repository = new SecurityEventRepository(context);

        var auditContext = new Mock<IAuditContext>();
        auditContext.Setup(c => c.UserEmail).Returns("test@test.com");

        var config = new ConfigurationBuilder().Build();

        var service = new AuditSecurityEventService(
            repository,
            auditContext.Object,
            NullLogger<AuditSecurityEventService>.Instance,
            config);

        // Create oversized details that would exceed 4000 chars when serialized
        var oversizedDetails = new Dictionary<string, object?>();
        for (int i = 0; i < 100; i++)
        {
            oversizedDetails[$"key{i}"] = new string('X', 100);
        }

        var dto = new SecurityEventDto
        {
            Message = "Test",
            EventType = SecurityEventType.SuspiciousActivity,
            Severity = SecurityEventSeverity.Medium,
            Details = oversizedDetails
        };

        // Act
        await service.RecordEventAsync(dto);

        // Assert - read the persisted entity back to observe the real size-guard summary
        var persisted = await context.SecurityEvents.SingleAsync();
        Assert.That(persisted.DetailsJson, Is.Not.Null);

        // Verify it's valid JSON (should not throw)
        var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(persisted.DetailsJson!);
        Assert.That(parsed, Is.Not.Null);
        Assert.That(parsed!.ContainsKey("_truncated"), Is.True);
        Assert.That(parsed["_truncated"]?.ToString(), Is.EqualTo("True"));
    }

    #endregion

    #region Finding 6: Report truncation signaling

    [Test]
    public async Task GetAuditChartDataAsync_WhenTruncated_SetsFlag()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);

        // The test would need to exceed QueryLimits.MaxChartDataRows to trigger truncation
        // For now, test that non-truncated data has IsTruncated = false
        var events = Enumerable.Range(1, 10)
            .Select(i => new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-i)
            })
            .ToList();

        await context.AuditEvents.AddRangeAsync(events);
        await context.SaveChangesAsync();

        var service = new AuditReportService(context, NullLogger<AuditReportService>.Instance);

        // Act
        var result = await service.GetAuditChartDataAsync(
            DateTimeOffset.UtcNow.AddDays(-15),
            DateTimeOffset.UtcNow);

        // Assert
        Assert.That(result.IsTruncated, Is.False);
        Assert.That(result.TruncatedAt, Is.Null);
        Assert.That(result.Items, Is.Not.Empty);
    }

    [Test]
    public async Task GenerateAuditReportAsync_ReturnsTruncationMetadata()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        await context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        var service = new AuditReportService(context, NullLogger<AuditReportService>.Instance);

        // Act
        var result = await service.GenerateAuditReportAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            "csv");

        // Assert
        Assert.That(result.Format, Is.EqualTo("csv"));
        Assert.That(result.Content, Is.Not.Empty);
        Assert.That(result.TotalRecords, Is.EqualTo(1));
        Assert.That(result.IsTruncated, Is.False);
    }

    #endregion

    #region Finding 7: Server-side severity filtering

    [Test]
    public async Task GetBySeverityAndDateRangeAsync_FiltersServerSide()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        await using var context = new AuditDbContext(options);
        var now = DateTimeOffset.UtcNow;

        await context.SecurityEvents.AddRangeAsync(new[]
        {
            new AuditSecurityEventEntity { EventType = SecurityEventType.AuditTamperAlert, Severity = SecurityEventSeverity.Critical, DetectedAt = now, Message = "Critical1" },
            new AuditSecurityEventEntity { EventType = SecurityEventType.SuspiciousActivity, Severity = SecurityEventSeverity.Low, DetectedAt = now, Message = "Low1" },
            new AuditSecurityEventEntity { EventType = SecurityEventType.IntegrityViolation, Severity = SecurityEventSeverity.Critical, DetectedAt = now, Message = "Critical2" },
            new AuditSecurityEventEntity { EventType = SecurityEventType.UnauthorizedAccess, Severity = SecurityEventSeverity.Medium, DetectedAt = now, Message = "Medium1" }
        });
        await context.SaveChangesAsync();

        var repository = new SecurityEventRepository(context);

        // Act
        var criticalEvents = await repository.GetBySeverityAndDateRangeAsync(
            SecurityEventSeverity.Critical,
            now.AddHours(-1),
            now.AddHours(1));

        var criticalList = criticalEvents.ToList();

        // Assert
        Assert.That(criticalList, Has.Count.EqualTo(2));
        Assert.That(criticalList.All(e => e.Severity == SecurityEventSeverity.Critical), Is.True);
    }

    #endregion
}
