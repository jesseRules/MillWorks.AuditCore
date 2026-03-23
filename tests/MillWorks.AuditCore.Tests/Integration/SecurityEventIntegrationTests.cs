using Mapster;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for AuditSecurityEventService verifying record, retrieve,
/// filter by severity, and resolve operations against a real SQLite backend.
/// </summary>
[TestFixture]
[Category("Integration")]
public class SecurityEventIntegrationTests : SqliteIntegrationFixture
{
    private IMapper _mapper = null!;

    [OneTimeSetUp]
    public void SetupMapper()
    {
        var config = new TypeAdapterConfig();
        new AuditMappingConfiguration().Register(config);
        _mapper = new Mapper(config);
    }

    [Test]
    public async Task RecordAndRetrieve_SecurityEvent_RoundTrip()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext
        {
            UserEmail = "admin@test.com",
            IpAddress = "10.0.0.1"
        };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.UnauthorizedAccess,
            Severity = SecurityEventSeverity.High,
            Message = "Unauthorized access attempt detected",
            Details = new Dictionary<string, object?>
            {
                ["Resource"] = "/api/admin",
                ["AttemptedBy"] = "unknown-user"
            }
        };

        // Act
        var recorded = await service.RecordEventAsync(dto);

        // Assert
        Assert.That(recorded, Is.Not.Null);
        Assert.That(recorded.EventType, Is.EqualTo(SecurityEventType.UnauthorizedAccess));
        Assert.That(recorded.Severity, Is.EqualTo(SecurityEventSeverity.High));
        Assert.That(recorded.Status, Is.EqualTo(SecurityEventStatus.Open));

        // Verify persisted in database
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(static e => e.Message == "Unauthorized access attempt detected");

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.EventType, Is.EqualTo(SecurityEventType.UnauthorizedAccess));
        Assert.That(persisted.DetectedBy, Is.EqualTo("admin@test.com"));
        Assert.That(persisted.IpAddress, Is.EqualTo("10.0.0.1"));
    }

    [Test]
    public async Task GetCriticalEvents_FiltersBySeverity()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext { UserEmail = "system@test.com" };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        // Seed events with mixed severities
        await context.Set<AuditSecurityEventEntity>().AddRangeAsync(
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.AuditTamperAlert,
                Severity = SecurityEventSeverity.Critical,
                Message = "Critical tamper alert",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.SuspiciousActivity,
                Severity = SecurityEventSeverity.Low,
                Message = "Low severity activity",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.IntegrityViolation,
                Severity = SecurityEventSeverity.Critical,
                Message = "Critical integrity violation",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.UnauthorizedAccess,
                Severity = SecurityEventSeverity.Medium,
                Message = "Medium unauthorized access",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            });
        await context.SaveChangesAsync();

        // Act
        var criticalEvents = (await service.GetCriticalEventsAsync(hours: 24)).ToList();

        // Assert - GetCriticalEventsAsync filters for Critical severity only
        Assert.That(criticalEvents, Has.Count.EqualTo(2));
        Assert.That(criticalEvents.All(static e => e.Severity == SecurityEventSeverity.Critical), Is.True);
    }

    [Test]
    public async Task ResolveEvent_UpdatesStatus()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext { UserEmail = "admin@test.com" };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        // Record an event first
        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.SuspiciousActivity,
            Severity = SecurityEventSeverity.Medium,
            Message = "Suspicious login pattern detected"
        };
        var recorded = await service.RecordEventAsync(dto);

        // Act
        var resolved = await service.ResolveEventAsync(
            recorded.Id,
            "Verified as legitimate user behavior",
            "security-admin@test.com");

        // Assert
        Assert.That(resolved, Is.Not.Null);
        Assert.That(resolved!.Status, Is.EqualTo(SecurityEventStatus.Resolved));
        Assert.That(resolved.Resolution, Is.EqualTo("Verified as legitimate user behavior"));
        Assert.That(resolved.ResolvedBy, Is.EqualTo("security-admin@test.com"));

        // Verify in database
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == recorded.Id);

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.Status, Is.EqualTo(SecurityEventStatus.Resolved));
        Assert.That(persisted.ResolvedAt, Is.Not.Null);
    }
}
