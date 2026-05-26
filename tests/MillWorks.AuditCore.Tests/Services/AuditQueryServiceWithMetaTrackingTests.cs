using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.Services.Decorator;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests for AuditQueryServiceWithMetaTracking verifying that all 6 query methods
/// delegate to the inner service and log meta-tracking calls with correct parameters.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditQueryServiceWithMetaTrackingTests
{
    private Mock<IAuditQueryService> _mockInner;
    private Mock<IAuditMetaTrackingService> _mockMetaTracking;
    private Mock<ILogger<AuditQueryServiceWithMetaTracking>> _mockLogger;
    private AuditQueryServiceWithMetaTracking _service;

    [SetUp]
    public void Setup()
    {
        _mockInner = new Mock<IAuditQueryService>();
        _mockMetaTracking = new Mock<IAuditMetaTrackingService>();
        _mockLogger = new Mock<ILogger<AuditQueryServiceWithMetaTracking>>();

        _mockMetaTracking
            .Setup(static m => m.LogAuditQueryAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _service = new AuditQueryServiceWithMetaTracking(
            _mockInner.Object,
            _mockMetaTracking.Object,
            _mockLogger.Object);
    }

    #region GetEntityAuditTrailAsync

    [Test]
    public async Task GetEntityAuditTrailAsync_DelegatesToInnerAndTracksQuery()
    {
        var entityId = Guid.NewGuid();
        var logs = new List<AuditLogDto> { new(), new() };

        _mockInner
            .Setup(x => x.GetEntityAuditTrailAsync("Patient", entityId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(logs);

        var result = await _service.GetEntityAuditTrailAsync("Patient", entityId);

        Assert.That(result.Count(), Is.EqualTo(2));

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "EntityTrail",
            It.Is<string>(s => s.Contains("Patient") && s.Contains(entityId.ToString())),
            "Entity History Review",
            2, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetUserActivityAsync

    [Test]
    public async Task GetUserActivityAsync_DelegatesToInnerAndTracksQuery()
    {
        var userId = Guid.NewGuid();
        var logs = new List<AuditLogDto> { new() };

        _mockInner
            .Setup(x => x.GetUserActivityAsync(userId, null, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(logs);

        var result = await _service.GetUserActivityAsync(userId);

        Assert.That(result.Count(), Is.EqualTo(1));

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "UserActivity",
            It.Is<string>(s => s.Contains(userId.ToString())),
            "User Activity Review",
            1, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetAuditEventsAsync

    [Test]
    public async Task GetAuditEventsAsync_DelegatesToInnerAndTracksQuery()
    {
        var response = new AuditEventsResponse
        {
            Items = new List<AuditEventDto> { new(), new(), new() }
        };

        _mockInner
            .Setup(static x => x.GetAuditEventsAsync(10, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _service.GetAuditEventsAsync(10, 25);

        Assert.That(result.Items, Has.Count.EqualTo(3));

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "BulkQuery",
            It.Is<string>(s => s.Contains("10") && s.Contains("25")),
            "Audit Review",
            3, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetAuditEventsAsync_WithNullItems_TracksZeroCount()
    {
        var response = new AuditEventsResponse { Items = null };

        _mockInner
            .Setup(static x => x.GetAuditEventsAsync(0, 50, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        await _service.GetAuditEventsAsync();

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "BulkQuery", It.IsAny<string>(), It.IsAny<string>(),
            0, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetAuditEventByIdAsync

    [Test]
    public async Task GetAuditEventByIdAsync_WhenFound_TracksCountOne()
    {
        var eventId = Guid.NewGuid();
        _mockInner
            .Setup(x => x.GetAuditEventByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditEventDto { EventId = eventId });

        var result = await _service.GetAuditEventByIdAsync(eventId);

        Assert.That(result, Is.Not.Null);

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "SingleEvent", It.IsAny<string>(), "Event Detail Review",
            1, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task GetAuditEventByIdAsync_WhenNotFound_TracksCountZero()
    {
        var eventId = Guid.NewGuid();
        _mockInner
            .Setup(x => x.GetAuditEventByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEventDto?)null);

        var result = await _service.GetAuditEventByIdAsync(eventId);

        Assert.That(result, Is.Null);

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "SingleEvent", It.IsAny<string>(), "Event Detail Review",
            0, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetRecentActivityAsync

    [Test]
    public async Task GetRecentActivityAsync_DelegatesToInnerAndTracksQuery()
    {
        var logs = new List<AuditLogDto> { new(), new() };

        _mockInner
            .Setup(static x => x.GetRecentActivityAsync(48, It.IsAny<CancellationToken>()))
            .ReturnsAsync(logs);

        var result = await _service.GetRecentActivityAsync(48);

        Assert.That(result.Count(), Is.EqualTo(2));

        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "RecentActivity",
            It.Is<string>(s => s.Contains("48")),
            "Recent Activity Monitoring",
            2, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region GetGroupedAuditTrailAsync

    [Test]
    public async Task GetGroupedAuditTrailAsync_DelegatesToInnerAndTracksQuery()
    {
        var entityId = Guid.NewGuid();
        var grouped = new Dictionary<string, List<AuditLogDto>>
        {
            ["Created"] = [new(), new()],
            ["Modified"] = [new()]
        };

        _mockInner
            .Setup(x => x.GetGroupedAuditTrailAsync("Order", entityId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(grouped);

        var result = await _service.GetGroupedAuditTrailAsync("Order", entityId);

        Assert.That(result, Has.Count.EqualTo(2));

        // Should track total count across all groups (2 + 1 = 3)
        _mockMetaTracking.Verify(m => m.LogAuditQueryAsync(
            "GroupedTrail",
            It.Is<string>(s => s.Contains("Order")),
            "Grouped History Review",
            3, It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
