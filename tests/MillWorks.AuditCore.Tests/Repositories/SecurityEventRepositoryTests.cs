using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for SecurityEventRepository filtering methods.
/// </summary>
[TestFixture]
public class SecurityEventRepositoryTests
{
    private DbContextOptions<AuditApplicationDbContext> _options;
    private AuditApplicationDbContext _context;
    private SecurityEventRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(_options);
        _repository = new SecurityEventRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region GetByEventTypeAsync

    /// <summary>
    /// Verifies filtering by security event type.
    /// </summary>
    [Test]
    public async Task GetByEventTypeAsync_ReturnsMatchingEvents()
    {
        // Arrange
        await SeedSecurityEvent(SecurityEventType.AuditTamperAlert, SecurityEventSeverity.High, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.UnauthorizedAccess, SecurityEventSeverity.Medium, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.AuditTamperAlert, SecurityEventSeverity.Critical, SecurityEventStatus.Investigating);

        // Act
        var results = (await _repository.GetByEventTypeAsync(SecurityEventType.AuditTamperAlert)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e => e.EventType == SecurityEventType.AuditTamperAlert), Is.True);
    }

    #endregion

    #region GetBySeverityAsync

    /// <summary>
    /// Verifies filtering by severity.
    /// </summary>
    [Test]
    public async Task GetBySeverityAsync_ReturnsMatchingEvents()
    {
        // Arrange
        await SeedSecurityEvent(SecurityEventType.SuspiciousActivity, SecurityEventSeverity.High, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.IntegrityViolation, SecurityEventSeverity.Low, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.ChainBroken, SecurityEventSeverity.High, SecurityEventStatus.Resolved);

        // Act
        var results = (await _repository.GetBySeverityAsync(SecurityEventSeverity.High)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
    }

    #endregion

    #region GetOpenEventsAsync

    /// <summary>
    /// Verifies only Open and Investigating status events are returned.
    /// </summary>
    [Test]
    public async Task GetOpenEventsAsync_ReturnsOpenAndInvestigatingOnly()
    {
        // Arrange
        await SeedSecurityEvent(SecurityEventType.SuspiciousActivity, SecurityEventSeverity.High, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.IntegrityViolation, SecurityEventSeverity.Medium, SecurityEventStatus.Investigating);
        await SeedSecurityEvent(SecurityEventType.ChainBroken, SecurityEventSeverity.Low, SecurityEventStatus.Resolved);
        await SeedSecurityEvent(SecurityEventType.UnauthorizedAccess, SecurityEventSeverity.Critical, SecurityEventStatus.FalsePositive);

        // Act
        var results = (await _repository.GetOpenEventsAsync()).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static e =>
            e.Status == SecurityEventStatus.Open || e.Status == SecurityEventStatus.Investigating), Is.True);
    }

    /// <summary>
    /// Verifies open events are sorted by severity descending, then by date.
    /// </summary>
    [Test]
    public async Task GetOpenEventsAsync_SortsBySeverityDescending()
    {
        // Arrange
        await SeedSecurityEvent(SecurityEventType.SuspiciousActivity, SecurityEventSeverity.Low, SecurityEventStatus.Open);
        await SeedSecurityEvent(SecurityEventType.IntegrityViolation, SecurityEventSeverity.Critical, SecurityEventStatus.Open);

        // Act
        var results = (await _repository.GetOpenEventsAsync()).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].Severity, Is.EqualTo(SecurityEventSeverity.Critical));
        Assert.That(results[1].Severity, Is.EqualTo(SecurityEventSeverity.Low));
    }

    #endregion

    #region GetByDateRangeAsync

    /// <summary>
    /// Verifies date range filtering for security events.
    /// </summary>
    [Test]
    public async Task GetByDateRangeAsync_ReturnsEventsInRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedSecurityEvent(SecurityEventType.SuspiciousActivity, SecurityEventSeverity.High, SecurityEventStatus.Open, now.AddDays(-5));
        await SeedSecurityEvent(SecurityEventType.IntegrityViolation, SecurityEventSeverity.Medium, SecurityEventStatus.Open, now.AddDays(-2));
        await SeedSecurityEvent(SecurityEventType.ChainBroken, SecurityEventSeverity.Low, SecurityEventStatus.Open, now.AddDays(1));

        // Act
        var results = (await _repository.GetByDateRangeAsync(now.AddDays(-3), now)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
    }

    #endregion

    #region GetByRelatedAuditEventAsync

    /// <summary>
    /// Verifies retrieval by related audit event ID.
    /// </summary>
    [Test]
    public async Task GetByRelatedAuditEventAsync_ReturnsMatchingEvent()
    {
        // Arrange
        var auditEvent = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(auditEvent);

        var securityEvent = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.AuditTamperAlert,
            Severity = SecurityEventSeverity.High,
            Status = SecurityEventStatus.Open,
            Message = "Tamper detected",
            DetectedAt = DateTimeOffset.UtcNow,
            RelatedAuditEventId = auditEvent.EventId
        };
        await _context.SecurityEvents.AddAsync(securityEvent);
        await _context.SaveChangesAsync();

        // Act
        var result = await _repository.GetByRelatedAuditEventAsync(auditEvent.EventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.RelatedAuditEventId, Is.EqualTo(auditEvent.EventId));
    }

    /// <summary>
    /// Verifies null return for non-existent related audit event.
    /// </summary>
    [Test]
    public async Task GetByRelatedAuditEventAsync_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByRelatedAuditEventAsync(Guid.NewGuid());

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region Helpers

    private async Task SeedSecurityEvent(
        SecurityEventType eventType,
        SecurityEventSeverity severity,
        SecurityEventStatus status,
        DateTimeOffset? detectedAt = null)
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = eventType,
            Severity = severity,
            Status = status,
            Message = $"Test {eventType}",
            DetectedAt = detectedAt ?? DateTimeOffset.UtcNow
        };
        await _context.SecurityEvents.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    #endregion
}
