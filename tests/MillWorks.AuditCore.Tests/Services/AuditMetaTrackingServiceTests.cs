using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Unit tests for AuditMetaTrackingService
/// </summary>
[TestFixture]
public class AuditMetaTrackingServiceTests
{
    /// <summary>
    /// Mock audit logger
    /// </summary>
    private Mock<IAuditLogger> _mockAuditLogger;

    /// <summary>
    /// Meta tracking service under test
    /// </summary>
    private AuditMetaTrackingService _metaTrackingService;

    /// <summary>
    /// Setup method to initialize test dependencies
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockAuditLogger = new Mock<IAuditLogger>();
        _metaTrackingService = new AuditMetaTrackingService(_mockAuditLogger.Object);
    }

    #region LogAuditQueryAsync Tests

    /// <summary>
    /// LogAuditQueryAsync_WithValidParameters_LogsSuccessfully
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithValidParameters_LogsSuccessfully()
    {
        // Arrange
        var queryType = "UserActivity";
        var queryParameters = "userId=123&startDate=2024-01-01";
        var purpose = "Compliance Review";
        var recordsReturned = 50;
        var justification = "Annual audit review";

        AuditEvent? capturedEvent = null;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            queryType,
            queryParameters,
            purpose,
            recordsReturned,
            justification);

        // Assert
        _mockAuditLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.EventType, Is.EqualTo($"AuditSystem.Query.{queryType}"));
        Assert.That(capturedEvent.CustomFields["QueryType"], Is.EqualTo(queryType));
        Assert.That(capturedEvent.CustomFields["QueryParameters"], Is.EqualTo(queryParameters));
        Assert.That(capturedEvent.CustomFields["Purpose"], Is.EqualTo(purpose));
        Assert.That(capturedEvent.CustomFields["RecordsReturned"], Is.EqualTo(recordsReturned));
        Assert.That(capturedEvent.CustomFields["Justification"], Is.EqualTo(justification));
    }

    /// <summary>
    /// LogAuditQueryAsync_WithCompliancePurpose_SetsComplianceFlagTrue
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithCompliancePurpose_SetsComplianceFlagTrue()
    {
        // Arrange
        var purpose = "GDPR compliance audit";
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            "UserData",
            "userId=123",
            purpose,
            10);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent.CustomFields["IsComplianceQuery"], Is.True);
    }

    /// <summary>
    /// LogAuditQueryAsync_WithSensitiveQueryType_SetsRequiresJustificationTrue
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithSensitiveQueryType_SetsRequiresJustificationTrue()
    {
        // Arrange
        var queryType = "PHIAccess";
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            queryType,
            "patientId=456",
            "Medical review",
            5);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["RequiresJustification"], Is.True);
    }

    /// <summary>
    /// LogAuditQueryAsync_WithSensitiveParameters_SetsSensitiveQueryFlagTrue
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithSensitiveParameters_SetsSensitiveQueryFlagTrue()
    {
        // Arrange
        var queryParameters = "executive=true&department=security";
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            "UserActivity",
            queryParameters,
            "Investigation",
            3);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["IsSensitiveQuery"], Is.True);
    }

    /// <summary>
    /// LogAuditQueryAsync_WithoutJustification_StoresNullJustification
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithoutJustification_StoresNullJustification()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            "StandardQuery",
            "basic=true",
            "Routine check",
            10);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["Justification"], Is.Null);
    }

    #endregion

    #region LogAuditExportAsync Tests

    /// <summary>
    /// LogAuditExportAsync_WithValidParameters_LogsSuccessfully
    /// </summary>
    [Test]
    public async Task LogAuditExportAsync_WithValidParameters_LogsSuccessfully()
    {
        // Arrange
        var exportType = "ComplianceReport";
        var startDate = DateTimeOffset.UtcNow.AddMonths(-1);
        var endDate = DateTimeOffset.UtcNow;
        var format = "PDF";
        var purpose = "Annual compliance review";
        var approvalReference = "APPROVAL-2024-001";

        AuditEvent? capturedEvent = null;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditExportAsync(
            exportType,
            startDate,
            endDate,
            format,
            purpose,
            approvalReference);

        // Assert
        _mockAuditLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent.EventType, Is.EqualTo("AuditSystem.Export"));
        Assert.That(capturedEvent.CustomFields["ExportType"], Is.EqualTo(exportType));
        Assert.That(capturedEvent.CustomFields["DateRangeStart"], Is.EqualTo(startDate));
        Assert.That(capturedEvent.CustomFields["DateRangeEnd"], Is.EqualTo(endDate));
        Assert.That(capturedEvent.CustomFields["Format"], Is.EqualTo(format));
        Assert.That(capturedEvent.CustomFields["Purpose"], Is.EqualTo(purpose));
        Assert.That(capturedEvent.CustomFields["ApprovalReference"], Is.EqualTo(approvalReference));
    }

    /// <summary>
    /// LogAuditExportAsync_WithApprovalReference_SetsRequiresApprovalFalse
    /// </summary>
    [Test]
    public async Task LogAuditExportAsync_WithApprovalReference_SetsRequiresApprovalFalse()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditExportAsync(
            "DataExport",
            DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow,
            "CSV",
            "Data analysis",
            "APPROVAL-123");

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["RequiresApproval"], Is.False);
    }

    /// <summary>
    /// LogAuditExportAsync_WithoutApprovalReference_SetsRequiresApprovalTrue
    /// </summary>
    [Test]
    public async Task LogAuditExportAsync_WithoutApprovalReference_SetsRequiresApprovalTrue()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditExportAsync(
            "DataExport",
            DateTimeOffset.UtcNow.AddDays(-7),
            DateTimeOffset.UtcNow,
            "CSV",
            "Data analysis");

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["RequiresApproval"], Is.True);
    }

    /// <summary>
    /// LogAuditExportAsync_Always_SetsDataGovernanceFlagTrue
    /// </summary>
    [Test]
    public async Task LogAuditExportAsync_Always_SetsDataGovernanceFlagTrue()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditExportAsync(
            "Export",
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow,
            "JSON",
            "Testing");

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["DataGovernanceFlag"], Is.True);
    }

    #endregion

    #region LogComplianceReportAccessAsync Tests

    /// <summary>
    /// LogComplianceReportAccessAsync_WithValidParameters_LogsSuccessfully
    /// </summary>
    [Test]
    public async Task LogComplianceReportAccessAsync_WithValidParameters_LogsSuccessfully()
    {
        // Arrange
        var standard = ComplianceStandard.GDPR;
        var reportPeriodStart = DateTimeOffset.UtcNow.AddYears(-1);
        var reportPeriodEnd = DateTimeOffset.UtcNow;
        var requestedBy = "john.doe@example.com";
        var purpose = "Annual compliance audit";

        AuditEvent? capturedEvent = null;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogComplianceReportAccessAsync(
            standard,
            reportPeriodStart,
            reportPeriodEnd,
            requestedBy,
            purpose);

        // Assert
        _mockAuditLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.EventType, Is.EqualTo("AuditSystem.ComplianceReport"));
        Assert.That(capturedEvent.CustomFields["Standard"], Is.EqualTo(standard.ToString()));
        Assert.That(capturedEvent.CustomFields["PeriodStart"], Is.EqualTo(reportPeriodStart));
        Assert.That(capturedEvent.CustomFields["PeriodEnd"], Is.EqualTo(reportPeriodEnd));
        Assert.That(capturedEvent.CustomFields["RequestedBy"], Is.EqualTo(requestedBy));
        Assert.That(capturedEvent.CustomFields["Purpose"], Is.EqualTo(purpose));
    }

    /// <summary>
    /// LogComplianceReportAccessAsync_WithHIPAAStandard_RecordsCorrectStandard
    /// </summary>
    [Test]
    public async Task LogComplianceReportAccessAsync_WithHIPAAStandard_RecordsCorrectStandard()
    {
        // Arrange
        var standard = ComplianceStandard.HIPAA;
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogComplianceReportAccessAsync(
            standard,
            DateTimeOffset.UtcNow.AddMonths(-6),
            DateTimeOffset.UtcNow,
            "auditor@example.com",
            "Healthcare compliance");

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["Standard"], Is.EqualTo("HIPAA"));
    }

    #endregion

    #region LogTamperDetectionCheckAsync Tests

    /// <summary>
    /// LogTamperDetectionCheckAsync_WithPassingCheck_LogsSuccessfully
    /// </summary>
    [Test]
    public async Task LogTamperDetectionCheckAsync_WithPassingCheck_LogsSuccessfully()
    {
        // Arrange
        var checkPassed = true;
        var eventsChecked = 1000;
        var tamperEventsFound = 0;
        var initiatedBy = "system-scheduler";

        AuditEvent? capturedEvent = null;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogTamperDetectionCheckAsync(
            checkPassed,
            eventsChecked,
            tamperEventsFound,
            initiatedBy);

        // Assert
        _mockAuditLogger.Verify(
            static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.EventType, Is.EqualTo("AuditSystem.TamperCheck"));
        Assert.That(capturedEvent.CustomFields["CheckPassed"], Is.True);
        Assert.That(capturedEvent.CustomFields["EventsChecked"], Is.EqualTo(eventsChecked));
        Assert.That(capturedEvent.CustomFields["TamperEventsFound"], Is.EqualTo(0));
        Assert.That(capturedEvent.CustomFields["InitiatedBy"], Is.EqualTo(initiatedBy));
        Assert.That(capturedEvent.CustomFields["RequiresAlert"], Is.False);
        Assert.That(capturedEvent.CustomFields["SecurityIncident"], Is.False);
    }

    /// <summary>
    /// LogTamperDetectionCheckAsync_WithTamperingDetected_SetsAlertFlags
    /// </summary>
    [Test]
    public async Task LogTamperDetectionCheckAsync_WithTamperingDetected_SetsAlertFlags()
    {
        // Arrange
        var checkPassed = false;
        var eventsChecked = 1000;
        var tamperEventsFound = 5;
        var initiatedBy = "security-admin";

        AuditEvent? capturedEvent = null;
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogTamperDetectionCheckAsync(
            checkPassed,
            eventsChecked,
            tamperEventsFound,
            initiatedBy);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent.CustomFields["CheckPassed"], Is.False);
        Assert.That(capturedEvent.CustomFields["TamperEventsFound"], Is.EqualTo(5));
        Assert.That(capturedEvent.CustomFields["RequiresAlert"], Is.True);
        Assert.That(capturedEvent.CustomFields["SecurityIncident"], Is.True);
    }

    /// <summary>
    /// LogTamperDetectionCheckAsync_WithNoTampering_DoesNotSetAlertFlags
    /// </summary>
    [Test]
    public async Task LogTamperDetectionCheckAsync_WithNoTampering_DoesNotSetAlertFlags()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogTamperDetectionCheckAsync(
            true,
            500,
            0,
            "automated-check");

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["RequiresAlert"], Is.False);
        Assert.That(capturedEvent.CustomFields["SecurityIncident"], Is.False);
    }

    #endregion

    #region Exception Handling Tests

    /// <summary>
    /// LogAuditQueryAsync_WhenLoggerThrows_PropagatesException
    /// </summary>
    [Test]
    public void LogAuditQueryAsync_WhenLoggerThrows_PropagatesException()
    {
        // Arrange
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Logger error"));

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _metaTrackingService.LogAuditQueryAsync(
                "TestQuery",
                "params=test",
                "Testing",
                10));
    }

    /// <summary>
    /// LogAuditExportAsync_WhenLoggerThrows_PropagatesException
    /// </summary>
    [Test]
    public void LogAuditExportAsync_WhenLoggerThrows_PropagatesException()
    {
        // Arrange
        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Logger error"));

        // Act & Assert
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _metaTrackingService.LogAuditExportAsync(
                "Export",
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow,
                "CSV",
                "Testing"));
    }

    #endregion

    #region Edge Cases

    /// <summary>
    /// LogAuditQueryAsync_WithEmptyParameters_HandlesGracefully
    /// </summary>
    [Test]
    public async Task LogAuditQueryAsync_WithEmptyParameters_HandlesGracefully()
    {
        // Arrange
        AuditEvent? capturedEvent = null;

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        await _metaTrackingService.LogAuditQueryAsync(
            "EmptyQuery",
            "",
            "Test",
            0);

        // Assert
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.CustomFields["QueryParameters"], Is.EqualTo(""));
        Assert.That(capturedEvent.CustomFields["RecordsReturned"], Is.EqualTo(0));
    }

    /// <summary>
    /// LogComplianceReportAccessAsync_WithAllStandards_LogsCorrectly
    /// </summary>
    [Test]
    public async Task LogComplianceReportAccessAsync_WithAllStandards_LogsCorrectly()
    {
        // Arrange
        var standards = new[]
        {
            ComplianceStandard.GDPR,
            ComplianceStandard.HIPAA,
            ComplianceStandard.SOC2,
            ComplianceStandard.ISO27001
        };

        var capturedEvents = new List<AuditEvent>();

        _mockAuditLogger
            .Setup(static x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEvent, CancellationToken>((evt, _) => capturedEvents.Add(evt))
            .Returns(Task.CompletedTask);

        // Act
        foreach (var standard in standards)
        {
            await _metaTrackingService.LogComplianceReportAccessAsync(
                standard,
                DateTimeOffset.UtcNow.AddMonths(-1),
                DateTimeOffset.UtcNow,
                "test@example.com",
                $"{standard} compliance check");
        }

        // Assert
        Assert.That(capturedEvents, Has.Count.EqualTo(4));
        Assert.That(capturedEvents.Select(static e => e.CustomFields["Standard"]),
            Is.EquivalentTo(standards.Select(static s => s.ToString())));
    }

    #endregion
}