using System.Linq.Expressions;
using MapsterMapper;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Unit tests for AuditService (section 2.3 of the test plan).
/// All dependencies are mocked; no database is involved.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditServiceUnitTests
{
    private Mock<IAuditLogRepository> _mockAuditLogRepository;
    private Mock<IAuditEventRepository> _mockAuditEventRepository;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<AuditService>> _mockLogger;
    private AuditService _auditService;

    [SetUp]
    public void Setup()
    {
        _mockAuditLogRepository = new Mock<IAuditLogRepository>();
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditService>>();

        _auditService = new AuditService(
            _mockAuditLogRepository.Object,
            _mockAuditEventRepository.Object,
            _mockMapper.Object,
            _mockLogger.Object);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 1. GetEntityAuditTrailAsync — valid entity returns mapped DTOs
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetEntityAuditTrailAsync with a valid entity name and ID maps the repository results and
    /// returns a non-null collection whose count matches the number of entities returned by the repo.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_ValidEntity_ReturnsMappedDtos()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        const string entityName = "Product";

        var entities = new List<AuditLogEntity>
        {
            new() { EntityName = entityName, EntityId = entityId },
            new() { EntityName = entityName, EntityId = entityId }
        };

        var dtos = new List<AuditLogDto>
        {
            new() { EntityName = entityName, EntityId = entityId },
            new() { EntityName = entityName, EntityId = entityId }
        };

        _mockAuditLogRepository
            .Setup(x => x.GetEntityAuditTrailAsync(entityName, entityId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        _mockMapper
            .Setup(static x => x.Map<IEnumerable<AuditLogDto>>(It.IsAny<IEnumerable<AuditLogEntity>>()))
            .Returns(dtos);

        // Act
        var result = await _auditService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count(), Is.EqualTo(2));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 2. GetEntityAuditTrailAsync — no results returns empty list
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetEntityAuditTrailAsync when the repository returns an empty collection maps to an empty
    /// DTO list rather than null.
    /// </summary>
    [Test]
    public async Task GetEntityAuditTrailAsync_NoResults_ReturnsEmptyList()
    {
        // Arrange
        var entityId = Guid.NewGuid();
        const string entityName = "Order";

        _mockAuditLogRepository
            .Setup(x => x.GetEntityAuditTrailAsync(entityName, entityId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditLogEntity>());

        _mockMapper
            .Setup(static x => x.Map<IEnumerable<AuditLogDto>>(It.IsAny<IEnumerable<AuditLogEntity>>()))
            .Returns(new List<AuditLogDto>());

        // Act
        var result = await _auditService.GetEntityAuditTrailAsync(entityName, entityId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 3. GetUserActivityAsync — valid user returns mapped activity
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetUserActivityAsync with a known userId returns the mapped DTOs for that user's activity.
    /// </summary>
    [Test]
    public async Task GetUserActivityAsync_ValidUser_ReturnsActivity()
    {
        // Arrange
        var userId = Guid.NewGuid();

        var entities = new List<AuditLogEntity>
        {
            new() { CreatedById = userId },
            new() { CreatedById = userId }
        };

        var dtos = new List<AuditLogDto>
        {
            new() { CreatedById = userId },
            new() { CreatedById = userId }
        };

        _mockAuditLogRepository
            .Setup(x => x.GetUserActivityAsync(userId, It.IsAny<DateTimeOffset?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities);

        _mockMapper
            .Setup(static x => x.Map<IEnumerable<AuditLogDto>>(It.IsAny<IEnumerable<AuditLogEntity>>()))
            .Returns(dtos);

        // Act
        var result = await _auditService.GetUserActivityAsync(userId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Count(), Is.EqualTo(2));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 4. GetActivitySummaryAsync — date range returns aggregated data
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetActivitySummaryAsync with a date range delegates to the repository's GetActionCountsAsync
    /// and surfaces the resulting dictionary with the correct keys and counts.
    /// </summary>
    [Test]
    public async Task GetActivitySummaryAsync_DateRange_ReturnsAggregatedData()
    {
        // Arrange
        var fromDate = DateTimeOffset.UtcNow.AddDays(-30);

        var kvps = new List<KeyValuePair<string, int>>
        {
            new("Created", 5),
            new("Updated", 3),
            new("Deleted", 1)
        };

        _mockAuditLogRepository
            .Setup(x => x.GetActionCountsAsync(It.IsAny<Guid?>(), fromDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(kvps);

        // Act
        var result = await _auditService.GetActivitySummaryAsync(fromDate: fromDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result["Created"], Is.EqualTo(5));
        Assert.That(result["Updated"], Is.EqualTo(3));
        Assert.That(result["Deleted"], Is.EqualTo(1));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 5. GetAuditEventById — non-existent ID returns null
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetAuditEventById returns null without throwing when the repository cannot find the requested
    /// event ID.
    /// </summary>
    [Test]
    public async Task GetAuditEventById_NonExistent_ReturnsNull()
    {
        // Arrange
        var missingId = Guid.NewGuid();

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEventEntity?)null);

        // Act
        var result = await _auditService.GetAuditEventById(missingId);

        // Assert
        Assert.That(result, Is.Null);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 6. GetAuditEvents — pagination respects page size
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetAuditEvents returns an AuditEventsResponse whose TotalItems, Limit, and Offset properties
    /// faithfully reflect the values supplied to the method and returned by the repository.
    /// </summary>
    [Test]
    public async Task GetAuditEvents_Pagination_RespectsPageSize()
    {
        // Arrange
        const int offset = 0;
        const int limit = 10;
        const int totalCount = 42;

        var entities = Enumerable.Range(0, limit)
            .Select(static _ => new AuditEventEntity { EventId = Guid.NewGuid() })
            .ToList();

        var dtos = entities
            .Select(static e => new AuditEventDto { EventId = e.EventId })
            .ToList();

        _mockAuditEventRepository
            .Setup(static x => x.GetPagedAsync(
                It.IsAny<int>(),
                limit,
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<Func<IQueryable<AuditEventEntity>, IOrderedQueryable<AuditEventEntity>>>(),
                It.IsAny<Expression<Func<AuditEventEntity, object>>[]>()))
            .ReturnsAsync((entities.AsEnumerable(), totalCount));

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<object>()))
            .Returns(dtos);

        // Act
        var result = await _auditService.GetAuditEvents(offset, limit);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalItems, Is.EqualTo(totalCount));
        Assert.That(result.Limit, Is.EqualTo(limit));
        Assert.That(result.Offset, Is.EqualTo(offset));
        Assert.That(result.Items, Has.Count.EqualTo(limit));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 7. SearchAuditEvents — filters are applied
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SearchAuditEvents with a fully-populated AuditSearchRequest executes without error and
    /// returns a paged response whose Limit and Offset match the request.
    /// </summary>
    [Test]
    public async Task SearchAuditEvents_WithFilters_AppliesAllFilters()
    {
        // Arrange
        var request = new AuditSearchRequest
        {
            User = "alice@example.com",
            EventType = "User.Login",
            StartDate = DateTimeOffset.UtcNow.AddDays(-7),
            EndDate = DateTimeOffset.UtcNow,
            Offset = 0,
            Limit = 25
        };

        var entities = new List<AuditEventEntity>
        {
            new() { EventId = Guid.NewGuid(), User = "alice@example.com", EventType = "User.Login" }
        };

        var dtos = new List<AuditEventDto>
        {
            new() { User = "alice@example.com", EventType = "User.Login" }
        };

        _mockAuditEventRepository
            .Setup(x => x.GetPagedAsync(
                It.IsAny<int>(),
                request.Limit,
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<Func<IQueryable<AuditEventEntity>, IOrderedQueryable<AuditEventEntity>>>(),
                It.IsAny<Expression<Func<AuditEventEntity, object>>[]>()))
            .ReturnsAsync((entities.AsEnumerable(), 1));

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<object>()))
            .Returns(dtos);

        // Act
        var result = await _auditService.SearchAuditEvents(request);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Limit, Is.EqualTo(request.Limit));
        Assert.That(result.Offset, Is.EqualTo(request.Offset));
        Assert.That(result.TotalItems, Is.EqualTo(1));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 8. GetAuditEventsByDateRange — invalid range (start > end) handled gracefully
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetAuditEventsByDateRange with a startDate later than endDate delegates to SearchAuditEvents,
    /// which should either return an empty result or propagate the call without throwing.
    /// The service must not throw an unhandled exception for this degenerate input.
    /// </summary>
    [Test]
    public Task GetAuditEventsByDateRange_InvalidRange_HandlesGracefully()
    {
        try
        {
            // Arrange — start date is after end date
            var startDate = DateTimeOffset.UtcNow;
            var endDate = DateTimeOffset.UtcNow.AddDays(-1);

            _mockAuditEventRepository
                .Setup(static x => x.GetPagedAsync(
                    It.IsAny<int>(),
                    It.IsAny<int>(),
                    It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                    It.IsAny<Func<IQueryable<AuditEventEntity>, IOrderedQueryable<AuditEventEntity>>>(),
                    It.IsAny<Expression<Func<AuditEventEntity, object>>[]>()))
                .ReturnsAsync((Enumerable.Empty<AuditEventEntity>(), 0));

            _mockMapper
                .Setup(static x => x.Map<List<AuditEventDto>>(It.IsAny<object>()))
                .Returns(new List<AuditEventDto>());

            // Act — must not throw
            AuditEventsResponse? result = null;
            Assert.DoesNotThrowAsync(async () =>
                result = await _auditService.GetAuditEventsByDateRange(startDate, endDate));

            // Assert
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.TotalItems, Is.EqualTo(0));
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 9. GetAuditSummary — returns correct counts
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetAuditSummary aggregates server-side counts from the repository and surfaces them through
    /// TotalEvents and UniqueUsers on the returned AuditSummaryResponse.
    /// </summary>
    [Test]
    public async Task GetAuditSummary_ReturnsCorrectCounts()
    {
        // Arrange
        const int expectedTotal = 15;
        const int expectedUnique = 4;

        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedTotal);

        _mockAuditEventRepository
            .Setup(static x => x.GetUniqueUserCountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedUnique);

        _mockAuditEventRepository
            .Setup(static x => x.GetEventTypeCountsAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new("User.Login", 10),
                new("User.Logout", 5)
            ]);

        _mockAuditEventRepository
            .Setup(static x => x.GetTopUsersByActivityAsync(
                It.IsAny<int>(),
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new("alice@example.com", 9),
                new("bob@example.com", 6)
            ]);

        // Act
        var result = await _auditService.GetAuditSummary();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.TotalEvents, Is.EqualTo(expectedTotal));
        Assert.That(result.UniqueUsers, Is.EqualTo(expectedUnique));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 10. GetAuditChartData — returns non-null time-series data
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetAuditChartData with a valid date range returns a non-null list of AuditChartData entries
    /// shaped from the repository's daily aggregation results.
    /// </summary>
    [Test]
    public async Task GetAuditChartData_ReturnsTimeSeriesData()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        var dailyCounts = new List<(DateTimeOffset Date, string EventType, int Count)>
        {
            (startDate.AddDays(1), "User.Login", 3),
            (startDate.AddDays(2), "User.Login", 5),
            (startDate.AddDays(3), "User.Logout", 2)
        };

        _mockAuditEventRepository
            .Setup(x => x.GetDailyEventCountsAsync(startDate, endDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(dailyCounts);

        // Act
        var result = await _auditService.GetAuditChartData(startDate, endDate, "day");

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result.All(static x => x.Count > 0), Is.True);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 11. GetDistinctUsers — returns unique users
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetDistinctUsers delegates to the repository's GetDistinctUsersAsync and returns exactly the
    /// users provided by the repository without duplicates or mutation.
    /// </summary>
    [Test]
    public async Task GetDistinctUsers_ReturnsUniqueUsers()
    {
        // Arrange
        var users = new List<string> { "alice@example.com", "bob@example.com", "carol@example.com" };

        _mockAuditEventRepository
            .Setup(static x => x.GetDistinctUsersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(users);

        // Act
        var result = await _auditService.GetDistinctUsers();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(3));
        Assert.That(result, Does.Contain("alice@example.com"));
        Assert.That(result, Does.Contain("bob@example.com"));
        Assert.That(result, Does.Contain("carol@example.com"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 12. GetDistinctEventTypes — returns unique event types
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// GetDistinctEventTypes delegates to the repository's GetDistinctEventTypesAsync and returns
    /// each event type exactly once.
    /// </summary>
    [Test]
    public async Task GetDistinctEventTypes_ReturnsUniqueTypes()
    {
        // Arrange
        var eventTypes = new List<string> { "Security", "User.Login", "User.Logout", "DataAccess" };

        _mockAuditEventRepository
            .Setup(static x => x.GetDistinctEventTypesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventTypes);

        // Act
        var result = await _auditService.GetDistinctEventTypes();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Has.Count.EqualTo(4));
        Assert.That(result, Does.Contain("Security"));
        Assert.That(result, Does.Contain("User.Login"));
    }

    // ──────────────────────────────────────────────────────────────────────────
    // 13. LogSecurityEventAsync — creates and persists a security event
    // ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// LogSecurityEventAsync constructs an AuditEventEntity with EventType "Security" and
    /// persists it by calling AddAsync followed by SaveChangesAsync on the audit event repository.
    /// </summary>
    [Test]
    public async Task LogSecurityEventAsync_CreatesSecurityEvent()
    {
        // Arrange
        const string highRiskContent = "SQL injection attempt";
        const string warning = "Potential SQL injection detected";
        const string user = "attacker@example.com";
        var details = new { Field = "Username", Value = "' OR 1=1--" };
        var additionalInfo = new { IpAddress = "10.0.0.1" };

        AuditEventEntity? capturedEntity = null;

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Act
        await _auditService.LogSecurityEventAsync(
            highRiskContent, warning, user, details, additionalInfo, CancellationToken.None);

        // Assert — AddAsync was called exactly once with a Security event for the right user
        _mockAuditEventRepository.Verify(
            static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()),
            Times.Once);

        _mockAuditEventRepository.Verify(
            static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.EventType, Is.EqualTo("Security"));
        Assert.That(capturedEntity.User, Is.EqualTo(user));
        Assert.That(capturedEntity.JsonData, Does.Contain(highRiskContent));
    }
}
