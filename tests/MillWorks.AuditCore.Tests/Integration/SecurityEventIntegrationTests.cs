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

    [Test]
    public async Task BreakGlassGranted_WithNormalizedFields_PersistsAndRoundTrips()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext { UserEmail = "superadmin@test.com" };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString();

        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass access granted for network policy recovery",
            TenantId = tenantId,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            CorrelationId = correlationId,
            Operation = "NetworkPolicyOverride",
            SourceIpHash = "sha256ofip12345",
            UserAgentHash = "sha256ofuseragent",
            Details = new Dictionary<string, object?>
            {
                ["BreakGlassGrantId"] = "grant-abc123",
                ["GrantTtlSeconds"] = 3600,
                ["AssuranceMethod"] = "PasskeyChallenge"
            }
        };

        // Act
        var recorded = await service.RecordEventAsync(dto);

        // Assert - recorded DTO
        Assert.That(recorded.EventType, Is.EqualTo(SecurityEventType.BreakGlassGranted));
        Assert.That(recorded.Severity, Is.EqualTo(SecurityEventSeverity.Critical));

        // Verify persisted in database with all normalized fields
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.CorrelationId == correlationId);

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.EventType, Is.EqualTo(SecurityEventType.BreakGlassGranted));
        Assert.That(persisted.TenantId, Is.EqualTo(tenantId));
        Assert.That(persisted.ActorUserId, Is.EqualTo(actorUserId));
        Assert.That(persisted.SubjectUserId, Is.EqualTo(subjectUserId));
        Assert.That(persisted.CorrelationId, Is.EqualTo(correlationId));
        Assert.That(persisted.Operation, Is.EqualTo("NetworkPolicyOverride"));
        Assert.That(persisted.SourceIpHash, Is.EqualTo("sha256ofip12345"));
        Assert.That(persisted.UserAgentHash, Is.EqualTo("sha256ofuseragent"));
        Assert.That(persisted.DetailsJson, Does.Contain("BreakGlassGrantId"));
        Assert.That(persisted.DetailsJson, Does.Contain("grant-abc123"));
    }

    [Test]
    public async Task GetCriticalEvents_ReturnsCriticalBreakGlassEvents()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext { UserEmail = "system@test.com" };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        // Seed break-glass events with mixed severities
        await context.Set<AuditSecurityEventEntity>().AddRangeAsync(
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.BreakGlassGranted,
                Severity = SecurityEventSeverity.Critical,
                Message = "Break-glass granted",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open,
                Operation = "NetworkPolicyOverride"
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.BreakGlassConsumed,
                Severity = SecurityEventSeverity.Critical,
                Message = "Break-glass consumed",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open,
                Operation = "AccessUsed"
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.BreakGlassPolicyChanged,
                Severity = SecurityEventSeverity.Critical,
                Message = "Policy changed",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open,
                Operation = "PolicyUpdate"
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.BreakGlassAttempt,
                Severity = SecurityEventSeverity.Medium,
                Message = "Break-glass attempt (not critical)",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            },
            new AuditSecurityEventEntity
            {
                EventType = SecurityEventType.BreakGlassExpired,
                Severity = SecurityEventSeverity.Low,
                Message = "Grant expired (low severity)",
                DetectedAt = DateTimeOffset.UtcNow,
                Status = SecurityEventStatus.Open
            });
        await context.SaveChangesAsync();

        // Act
        var criticalEvents = (await service.GetCriticalEventsAsync(hours: 24)).ToList();

        // Assert - only critical break-glass events should be returned
        Assert.That(criticalEvents, Has.Count.EqualTo(3));
        Assert.That(criticalEvents.All(static e => e.Severity == SecurityEventSeverity.Critical), Is.True);
        Assert.That(criticalEvents.Any(static e => e.EventType == SecurityEventType.BreakGlassGranted), Is.True);
        Assert.That(criticalEvents.Any(static e => e.EventType == SecurityEventType.BreakGlassConsumed), Is.True);
        Assert.That(criticalEvents.Any(static e => e.EventType == SecurityEventType.BreakGlassPolicyChanged), Is.True);
    }

    [Test]
    public async Task HashOnlyBreakGlass_DoesNotPersistRawIp()
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext
        {
            UserEmail = "superadmin@test.com",
            IpAddress = "192.168.1.100"
        };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass with hash-only metadata",
            SourceIpHash = "sha256hashofactualip",
            IpAddress = null
        };

        // Act
        var recorded = await service.RecordEventAsync(dto);

        // Assert - raw IP from auditContext should NOT be stamped
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == recorded.Id);

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.IpAddress, Is.Null,
            "When SourceIpHash is set and IpAddress is null, raw IP should not be persisted");
        Assert.That(persisted.SourceIpHash, Is.EqualTo("sha256hashofactualip"));
    }

    [Test]
    public async Task DetailsJson_ParsesBackToDetails_OnRead()
    {
        // Arrange - directly seed an entity with DetailsJson
        using var context = CreateContext();
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Test event",
            DetectedAt = DateTimeOffset.UtcNow,
            Status = SecurityEventStatus.Open,
            DetailsJson = """{"BreakGlassGrantId":"grant-999","GrantTtlSeconds":7200}"""
        };
        await context.Set<AuditSecurityEventEntity>().AddAsync(entity);
        await context.SaveChangesAsync();

        // Act - read back via mapper
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == entity.Id);

        var dto = _mapper.Map<SecurityEventDto>(persisted!);

        // Assert
        Assert.That(dto.Details, Is.Not.Null);
        Assert.That(dto.Details, Has.Count.EqualTo(2));
        Assert.That(dto.Details["BreakGlassGrantId"], Is.EqualTo("grant-999"));
        Assert.That(dto.Details["GrantTtlSeconds"], Is.EqualTo(7200L));
    }

    [TestCase(SecurityEventType.BreakGlassAttempt)]
    [TestCase(SecurityEventType.BreakGlassDenied)]
    [TestCase(SecurityEventType.BreakGlassChallengeIssued)]
    [TestCase(SecurityEventType.BreakGlassChallengeFailed)]
    [TestCase(SecurityEventType.BreakGlassGranted)]
    [TestCase(SecurityEventType.BreakGlassConsumed)]
    [TestCase(SecurityEventType.BreakGlassExpired)]
    [TestCase(SecurityEventType.BreakGlassRevoked)]
    [TestCase(SecurityEventType.BreakGlassPolicyChanged)]
    [TestCase(SecurityEventType.BreakGlassEnrollmentChanged)]
    public async Task AllBreakGlassEventTypes_PersistAndRetrieve(SecurityEventType eventType)
    {
        // Arrange
        using var context = CreateContext();
        var securityRepo = new SecurityEventRepository(context);
        var auditContext = new AuditContext { UserEmail = "admin@test.com" };
        var appConfig = new ConfigurationBuilder().AddInMemoryCollection().Build();

        var service = new AuditSecurityEventService(
            securityRepo, auditContext, _mapper,
            NullLogger<AuditSecurityEventService>.Instance, appConfig);

        var dto = new SecurityEventDto
        {
            EventType = eventType,
            Severity = SecurityEventSeverity.High,
            Message = $"Test event for {eventType}"
        };

        // Act
        var recorded = await service.RecordEventAsync(dto);

        // Assert
        Assert.That(recorded.EventType, Is.EqualTo(eventType));

        // Verify in database
        using var verifyContext = CreateContext();
        var persisted = await verifyContext.Set<AuditSecurityEventEntity>()
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == recorded.Id);

        Assert.That(persisted, Is.Not.Null);
        Assert.That(persisted!.EventType, Is.EqualTo(eventType));
    }
}
