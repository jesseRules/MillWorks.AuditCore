using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.SampleProject.Controllers;

/// <summary>
/// API endpoints for testing the MillWorks Audit library
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class AuditController(
    IAuditService auditService,
    IAuditLogger auditLogger,
    IAuditQueryService queryService,
    IAuditSearchService searchService,
    IAuditReportService reportService,
    ITamperDetectionService tamperDetection,
    IAuditComplianceService complianceService,
    IAuditArchivalService archivalService,
    ILogger<AuditController> logger)
    : ControllerBase
{
    #region Basic Audit Operations

    /// <summary>
    /// Create a test audit event
    /// </summary>
    [HttpPost("test-event")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateTestEvent([FromBody] TestEventRequest request)
    {
        await auditLogger.LogAsync(
            request.EventType ?? "Test.Event",
            new
            {
                request.Message,
                TestData = request.Data,
                Timestamp = DateTimeOffset.UtcNow
            });
        
        logger.LogInformation("Test audit event logged: {EventType}", request.EventType ?? "Test.Event");

        return Ok(new { Success = true, Message = "Test audit event created successfully" });
    }

    /// <summary>
    /// Create a test audit event using scope
    /// </summary>
    [HttpPost("test-event-scope")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateTestEventWithScope([FromBody] TestEventRequest request)
    {
        await using var scope = auditLogger.CreateScope(
            request.EventType ?? "Test.ScopeEvent",
            request.Data);

        scope.SetCustomField("Message", request.Message);
        scope.SetCustomField("ProcessingTime", DateTimeOffset.UtcNow);

        // Simulate some work
        await Task.Delay(100);

        scope.SetCustomField("Completed", true);

        return Ok(new { Success = true, Message = "Test audit event with scope created successfully" });
    }

    /// <summary>
    /// Create a test operation
    /// </summary>
    [HttpPost("test-operation")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateTestOperation([FromBody] TestEventRequest request)
    {
        var operationId = await auditLogger.BeginOperationAsync(
            request.EventType ?? "Test.Operation",
            new { StartData = request.Data });

        try
        {
            // Simulate some work
            await Task.Delay(100);

            await auditLogger.EndOperationAsync(
                operationId,
                success: true,
                result: new { request.Message, Completed = true });

            return Ok(new { Success = true, OperationId = operationId });
        }
        catch (Exception ex)
        {
            await auditLogger.EndOperationAsync(
                operationId,
                success: false,
                result: new { Error = ex.Message });
            throw;
        }
    }

    #endregion

    #region Query Operations

    /// <summary>
    /// Get all audit events with pagination
    /// </summary>
    [HttpGet("events")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditEvents(
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var events = await auditService.GetAuditEvents(offset, limit);
        return Ok(events);
    }

    /// <summary>
    /// Get a specific audit event by ID
    /// </summary>
    [HttpGet("events/{eventId:guid}")]
    [ProducesResponseType(typeof(AuditEventDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAuditEvent(Guid eventId)
    {
        var auditEvent = await auditService.GetAuditEventById(eventId);

        if (auditEvent == null)
            return NotFound(new { Message = $"Audit event {eventId} not found" });

        return Ok(auditEvent);
    }

    /// <summary>
    /// Search audit events
    /// </summary>
    [HttpPost("search")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchAuditEvents([FromBody] AuditSearchRequest request)
    {
        var results = await auditService.SearchAuditEvents(request);
        return Ok(results);
    }

    /// <summary>
    /// Get audit events by date range
    /// </summary>
    [HttpGet("events/by-date")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditEventsByDateRange(
        [FromQuery] DateTimeOffset startDate,
        [FromQuery] DateTimeOffset endDate,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var events = await auditService.GetAuditEventsByDateRange(
            startDate, endDate, offset, limit);
        return Ok(events);
    }

    /// <summary>
    /// Get audit events by user
    /// </summary>
    [HttpGet("events/by-user/{username}")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditEventsByUser(
        string username,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var events = await auditService.GetAuditEventsByUser(username, offset, limit);
        return Ok(events);
    }

    /// <summary>
    /// Get entity audit trail
    /// </summary>
    [HttpGet("trail/{entityName}/{entityId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEntityAuditTrail(string entityName, Guid entityId)
    {
        var trail = await queryService.GetEntityAuditTrailAsync(entityName, entityId);
        return Ok(trail);
    }

    /// <summary>
    /// Get user activity
    /// </summary>
    [HttpGet("activity/user/{userId:guid}")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetUserActivity(
        Guid userId,
        [FromQuery] DateTimeOffset? fromDate = null,
        [FromQuery] int take = 50)
    {
        var activity = await queryService.GetUserActivityAsync(userId, fromDate, take);
        return Ok(activity);
    }

    /// <summary>
    /// Get recent activity
    /// </summary>
    [HttpGet("activity/recent")]
    [ProducesResponseType(typeof(IEnumerable<AuditLogDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentActivity([FromQuery] int hours = 24)
    {
        var activity = await queryService.GetRecentActivityAsync(hours);
        return Ok(activity);
    }

    #endregion

    #region Reporting Operations

    /// <summary>
    /// Get audit summary
    /// </summary>
    [HttpGet("summary")]
    [ProducesResponseType(typeof(AuditSummaryResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAuditSummary(
        [FromQuery] DateTimeOffset? startDate = null,
        [FromQuery] DateTimeOffset? endDate = null)
    {
        var summary = await auditService.GetAuditSummary(startDate, endDate);
        return Ok(summary);
    }

    /// <summary>
    /// Get chart data
    /// </summary>
    [HttpGet("chart-data")]
    [ProducesResponseType(typeof(List<AuditChartData>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChartData(
        [FromQuery] DateTimeOffset startDate,
        [FromQuery] DateTimeOffset endDate,
        [FromQuery] string groupBy = "day")
    {
        var chartData = await auditService.GetAuditChartData(startDate, endDate, groupBy);
        return Ok(chartData);
    }

    /// <summary>
    /// Get activity summary
    /// </summary>
    [HttpGet("activity/summary")]
    [ProducesResponseType(typeof(Dictionary<string, int>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetActivitySummary(
        [FromQuery] Guid? userId = null,
        [FromQuery] DateTimeOffset? fromDate = null)
    {
        var summary = await reportService.GetActivitySummaryAsync(userId, fromDate);
        return Ok(summary);
    }

    /// <summary>
    /// Get event type distribution
    /// </summary>
    [HttpGet("distribution/event-types")]
    [ProducesResponseType(typeof(List<AuditEventTypeCount>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEventTypeDistribution(
        [FromQuery] DateTimeOffset? startDate = null,
        [FromQuery] DateTimeOffset? endDate = null)
    {
        var distribution = await reportService.GetEventTypeDistributionAsync(startDate, endDate);
        return Ok(distribution);
    }

    /// <summary>
    /// Get top users by activity
    /// </summary>
    [HttpGet("top-users")]
    [ProducesResponseType(typeof(List<AuditUserCount>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTopUsers(
        [FromQuery] int count = 10,
        [FromQuery] DateTimeOffset? startDate = null,
        [FromQuery] DateTimeOffset? endDate = null)
    {
        var topUsers = await reportService.GetTopUsersAsync(count, startDate, endDate);
        return Ok(topUsers);
    }

    #endregion

    #region Search Operations

    /// <summary>
    /// Get distinct users
    /// </summary>
    [HttpGet("distinct/users")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDistinctUsers()
    {
        var users = await auditService.GetDistinctUsers();
        return Ok(users);
    }

    /// <summary>
    /// Get distinct event types
    /// </summary>
    [HttpGet("distinct/event-types")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDistinctEventTypes()
    {
        var eventTypes = await auditService.GetDistinctEventTypes();
        return Ok(eventTypes);
    }

    /// <summary>
    /// Search by entity
    /// </summary>
    [HttpGet("search/entity/{entityType}")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchByEntity(
        string entityType,
        [FromQuery] string? entityId = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var results = await searchService.SearchByEntityAsync(
            entityType, entityId, offset, limit);
        return Ok(results);
    }

    /// <summary>
    /// Get security-related audit events (a filtered convenience query over search)
    /// </summary>
    [HttpGet("security")]
    [ProducesResponseType(typeof(AuditEventsResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSecurityEvents(
        [FromQuery] string eventType = "Security",
        [FromQuery] string? user = null,
        [FromQuery] DateTimeOffset? startDate = null,
        [FromQuery] DateTimeOffset? endDate = null,
        [FromQuery] int offset = 0,
        [FromQuery] int limit = 50)
    {
        var request = new AuditSearchRequest
        {
            EventType = eventType,
            User = user,
            StartDate = startDate,
            EndDate = endDate,
            Offset = offset,
            Limit = limit
        };

        var results = await auditService.SearchAuditEvents(request);
        return Ok(results);
    }

    /// <summary>
    /// Get audit chain integrity status: sequence check, recent tamper alerts, and archive count
    /// </summary>
    [HttpGet("chain/status")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetChainStatus()
    {
        // Sequential awaits -- the audit DbContext is not thread-safe
        var isHealthy = await tamperDetection.VerifySequenceIntegrityAsync();
        var alerts = await tamperDetection.DetectTamperingAsync(24);
        var archives = await archivalService.GetArchivesAsync();

        return Ok(new
        {
            IsHealthy = isHealthy,
            RecentAlertCount = alerts.Count,
            RecentAlerts = alerts,
            ArchiveCount = archives.Count,
            CheckedAt = DateTimeOffset.UtcNow
        });
    }

    #endregion

    #region Tamper Detection Operations

    /// <summary>
    /// Verify event integrity
    /// </summary>
    [HttpGet("integrity/verify/{eventId:guid}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyEventIntegrity(Guid eventId)
    {
        var isValid = await tamperDetection.VerifyIntegrityAsync(eventId);
        return Ok(new
        {
            EventId = eventId,
            IsValid = isValid,
            Message = isValid ? "Event integrity verified" : "Event integrity check failed"
        });
    }

    /// <summary>
    /// Verify chain integrity
    /// </summary>
    [HttpPost("integrity/verify-chain")]
    [ProducesResponseType(typeof(TamperDetectionResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifyChainIntegrity(
        [FromQuery] DateTimeOffset? startDate = null,
        [FromQuery] DateTimeOffset? endDate = null)
    {
        var result = await tamperDetection.VerifyChainIntegrityAsync(startDate, endDate);
        return Ok(result);
    }

    /// <summary>
    /// Verify sequence integrity
    /// </summary>
    [HttpGet("integrity/verify-sequence")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> VerifySequenceIntegrity()
    {
        var isValid = await tamperDetection.VerifySequenceIntegrityAsync();
        return Ok(new
        {
            IsValid = isValid,
            Message = isValid ? "Sequence integrity verified" : "Sequence integrity check failed"
        });
    }

    /// <summary>
    /// Detect tampering
    /// </summary>
    [HttpGet("integrity/detect-tampering")]
    [ProducesResponseType(typeof(List<TamperAlert>), StatusCodes.Status200OK)]
    public async Task<IActionResult> DetectTampering([FromQuery] int hoursBack = 24)
    {
        var alerts = await tamperDetection.DetectTamperingAsync(hoursBack);
        return Ok(alerts);
    }

    /// <summary>
    /// Export integrity proof
    /// </summary>
    [HttpGet("integrity/export-proof/{eventId:guid}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportIntegrityProof(Guid eventId)
    {
        var proof = await tamperDetection.ExportIntegrityProofAsync(eventId);
        return File(proof, "application/json", $"integrity-proof-{eventId}.json");
    }

    #endregion

    #region Compliance Operations

    /// <summary>
    /// Generate compliance report
    /// </summary>
    [HttpPost("compliance/report")]
    [ProducesResponseType(typeof(ComplianceReport), StatusCodes.Status200OK)]
    public async Task<IActionResult> GenerateComplianceReport(
        [FromBody] ComplianceReportRequest request)
    {
        var report = await complianceService.GenerateComplianceReportAsync(
            request.Standard,
            request.StartDate,
            request.EndDate);

        return Ok(report);
    }

    /// <summary>
    /// Anonymize user data
    /// </summary>
    [HttpPost("compliance/anonymize/{userId:guid}")]
    [ProducesResponseType(typeof(AnonymizationResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> AnonymizeUserData(Guid userId)
    {
        var result = await complianceService.AnonymizeUserDataAsync(userId);
        return Ok(result);
    }

    /// <summary>
    /// Export user audit data
    /// </summary>
    [HttpGet("compliance/export/{userId:guid}")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ExportUserAuditData(Guid userId)
    {
        var result = await complianceService.ExportUserAuditDataAsync(userId);

        if (!result.Success)
            return BadRequest(new { result.Message });

        return File(result.Data, "application/json", result.FileName);
    }

    /// <summary>
    /// Validate retention compliance
    /// </summary>
    [HttpGet("compliance/validate-retention")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateRetentionCompliance()
    {
        var isCompliant = await complianceService.ValidateRetentionComplianceAsync();
        return Ok(new
        {
            IsCompliant = isCompliant,
            Message = isCompliant
                ? "Retention policies are compliant"
                : "Retention policy violations detected"
        });
    }

    #endregion

    #region Archival Operations

    /// <summary>
    /// Archive old audit events
    /// </summary>
    [HttpPost("archive")]
    [ProducesResponseType(typeof(AuditArchivalResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> ArchiveAuditEvents([FromBody] ArchiveRequest request)
    {
        var result = await archivalService.ArchiveAuditEventsAsync(request.ArchiveBefore);
        return Ok(result);
    }

    /// <summary>
    /// Restore archived events
    /// </summary>
    [HttpPost("archive/restore/{archiveId}")]
    [ProducesResponseType(typeof(AuditRestoreResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> RestoreArchivedEvents(string archiveId)
    {
        var result = await archivalService.RestoreArchivedEventsAsync(archiveId);
        return Ok(result);
    }

    /// <summary>
    /// Get all archives
    /// </summary>
    [HttpGet("archives")]
    [ProducesResponseType(typeof(List<ArchiveMetadata>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetArchives()
    {
        var archives = await archivalService.GetArchivesAsync();
        return Ok(archives);
    }

    /// <summary>
    /// Validate archive integrity
    /// </summary>
    [HttpGet("archive/validate/{archiveId}")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    public async Task<IActionResult> ValidateArchiveIntegrity(string archiveId)
    {
        var isValid = await archivalService.ValidateArchiveIntegrityAsync(archiveId);
        return Ok(new
        {
            ArchiveId = archiveId,
            IsValid = isValid,
            Message = isValid ? "Archive integrity verified" : "Archive integrity check failed"
        });
    }

    #endregion
}

#region Request Models

/// <summary>
/// Test event request model
/// </summary>
public sealed class TestEventRequest
{
    /// <summary>
    /// Event type for the test event
    /// </summary>
    public string? EventType { get; set; }

    /// <summary>
    /// Message for the event
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Data dictionary for the event
    /// </summary>
    public Dictionary<string, object>? Data { get; set; }
}

/// <summary>
/// Compliance report request model
/// </summary>
public sealed class ComplianceReportRequest
{
    /// <summary>
    /// Standard for the compliance report
    /// </summary>
    public ComplianceStandard Standard { get; set; }

    /// <summary>
    /// Start date for the compliance report
    /// </summary>
    public DateTimeOffset StartDate { get; set; }

    /// <summary>
    /// End date for the compliance report
    /// </summary>
    public DateTimeOffset EndDate { get; set; }
}

/// <summary>
/// Archive request model
/// </summary>
public sealed class ArchiveRequest
{
    /// <summary>
    /// Archive events before this date
    /// </summary>
    public DateTimeOffset ArchiveBefore { get; set; }
}

#endregion