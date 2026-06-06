using MapsterMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Services;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
[Category("Unit")]
public class AuditSecurityEventServiceTests
{
    private Mock<ISecurityEventRepository> _mockRepository;
    private IAuditContext _auditContext;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<AuditSecurityEventService>> _mockLogger;
    private IConfiguration _configuration;
    private IAuditSecurityEventService _service;

    [SetUp]
    public void Setup()
    {
        _mockRepository = new Mock<ISecurityEventRepository>();
        _auditContext = new AuditContext();
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditSecurityEventService>>();
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Security:AlertsEnabled", "true" }
            })
            .Build();

        _service = new AuditSecurityEventService(
            _mockRepository.Object,
            _auditContext,
            _mockMapper.Object,
            _mockLogger.Object,
            _configuration);
    }

    [Test]
    public async Task RecordEventAsync_ValidEvent_PersistsAndReturnsDto()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.UnauthorizedAccess,
            Severity = SecurityEventSeverity.High,
            Message = "Unauthorized access attempt",
            Details = new Dictionary<string, object?> { { "Resource", "/admin" } }
        };

        var entity = new AuditSecurityEventEntity();
        var resultDto = new SecurityEventDto { Id = Guid.NewGuid(), Message = "Unauthorized access attempt" };

        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(resultDto);

        var result = await _service.RecordEventAsync(inputDto);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Message, Is.EqualTo("Unauthorized access attempt"));
        _mockRepository.Verify(r => r.AddAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepository.Verify(static r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RecordEventAsync_SetsDetectedAtAndStatus()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.SuspiciousActivity,
            Severity = SecurityEventSeverity.Medium,
            Message = "Suspicious login"
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        _auditContext.UserEmail = "admin@example.com";
        _auditContext.IpAddress = "10.0.0.1";

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.DetectedAt, Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(entity.DetectedBy, Is.EqualTo("admin@example.com"));
        Assert.That(entity.IpAddress, Is.EqualTo("10.0.0.1"));
        Assert.That(entity.Status, Is.EqualTo(SecurityEventStatus.Open));
    }

    [Test]
    public async Task RecordEventAsync_NoUserContext_SetsDetectedByToSystem()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.IntegrityViolation,
            Severity = SecurityEventSeverity.Low,
            Message = "Minor integrity check"
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.DetectedBy, Is.EqualTo("System"));
    }

    [Test]
    public async Task RecordEventAsync_CriticalSeverity_SendsAlert()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.AuditTamperAlert,
            Severity = SecurityEventSeverity.Critical,
            Message = "Audit tamper detected!"
        };

        var entity = new AuditSecurityEventEntity { Severity = SecurityEventSeverity.Critical };
        var resultDto = new SecurityEventDto { Severity = SecurityEventSeverity.Critical };

        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(resultDto);

        await _service.RecordEventAsync(inputDto);

        // SendAlertAsync is called internally for critical events - verify via logger
        _mockLogger.Verify(static x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task GetCriticalEventsAsync_ReturnsCriticalOnly()
    {
        var criticalEntity = new AuditSecurityEventEntity
        {
            Severity = SecurityEventSeverity.Critical,
            DetectedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };
        var lowEntity = new AuditSecurityEventEntity
        {
            Severity = SecurityEventSeverity.Low,
            DetectedAt = DateTimeOffset.UtcNow.AddHours(-1)
        };

        _mockRepository.Setup(static r => r.GetByDateRangeAsync(
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditSecurityEventEntity> { criticalEntity, lowEntity });

        var criticalDtos = new List<SecurityEventDto>
        {
            new() { Severity = SecurityEventSeverity.Critical }
        };

        _mockMapper.Setup(static m => m.Map<IEnumerable<SecurityEventDto>>(
                It.Is<IEnumerable<AuditSecurityEventEntity>>(static e =>
                    e.All(static x => x.Severity == SecurityEventSeverity.Critical))))
            .Returns(criticalDtos);

        var result = await _service.GetCriticalEventsAsync(24);

        Assert.That(result.Count(), Is.EqualTo(1));
    }

    [Test]
    public async Task ResolveEventAsync_ExistingEvent_MarksResolved()
    {
        var eventId = Guid.NewGuid();
        var entity = new AuditSecurityEventEntity
        {
            Status = SecurityEventStatus.Open,
            Message = "Test event"
        };
        var resultDto = new SecurityEventDto
        {
            Status = SecurityEventStatus.Resolved,
            Resolution = "Fixed"
        };

        _mockRepository.Setup(r => r.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(resultDto);

        var result = await _service.ResolveEventAsync(eventId, "Fixed", "admin@example.com");

        Assert.That(result, Is.Not.Null);
        Assert.That(entity.Status, Is.EqualTo(SecurityEventStatus.Resolved));
        Assert.That(entity.Resolution, Is.EqualTo("Fixed"));
        Assert.That(entity.ResolvedBy, Is.EqualTo("admin@example.com"));
        Assert.That(entity.ResolvedAt, Is.Not.Null);
        _mockRepository.Verify(r => r.UpdateAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
        _mockRepository.Verify(static r => r.SaveChangesAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ResolveEventAsync_NonExistentEvent_ReturnsNull()
    {
        var eventId = Guid.NewGuid();

        _mockRepository.Setup(r => r.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditSecurityEventEntity?)null);

        var result = await _service.ResolveEventAsync(eventId, "Fixed", "admin");

        Assert.That(result, Is.Null);
        _mockRepository.Verify(
            static r => r.UpdateAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task GetOpenEventsAsync_DelegatesToRepository()
    {
        var openEntities = new List<AuditSecurityEventEntity>
        {
            new() { Status = SecurityEventStatus.Open },
            new() { Status = SecurityEventStatus.Investigating }
        };
        var openDtos = new List<SecurityEventDto>
        {
            new() { Status = SecurityEventStatus.Open },
            new() { Status = SecurityEventStatus.Investigating }
        };

        _mockRepository.Setup(static r => r.GetOpenEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(openEntities);
        _mockMapper.Setup(m => m.Map<IEnumerable<SecurityEventDto>>(openEntities))
            .Returns(openDtos);

        var result = await _service.GetOpenEventsAsync();

        Assert.That(result.Count(), Is.EqualTo(2));
        _mockRepository.Verify(static r => r.GetOpenEventsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SendAlertAsync_AlertsDisabled_DoesNotLog()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "Security:AlertsEnabled", "false" }
            })
            .Build();

        var service = new AuditSecurityEventService(
            _mockRepository.Object,
            _auditContext,
            _mockMapper.Object,
            _mockLogger.Object,
            config);

        var securityEvent = new SecurityEventDto
        {
            Id = Guid.NewGuid(),
            EventType = SecurityEventType.AuditTamperAlert,
            Message = "Test alert"
        };

        await service.SendAlertAsync(securityEvent);

        _mockLogger.Verify(static x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Test]
    public async Task SendAlertAsync_AlertsEnabled_LogsCritical()
    {
        var securityEvent = new SecurityEventDto
        {
            Id = Guid.NewGuid(),
            EventType = SecurityEventType.DataExfiltration,
            Message = "Data exfiltration attempt"
        };

        await _service.SendAlertAsync(securityEvent);

        _mockLogger.Verify(static x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public async Task RecordEventAsync_WithDetails_SerializesDetailsJson()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.PrivilegeEscalation,
            Severity = SecurityEventSeverity.High,
            Message = "Privilege escalation",
            Details = new Dictionary<string, object?>
            {
                { "UserId", "user-123" },
                { "TargetRole", "Admin" }
            }
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.DetailsJson, Is.Not.Null);
        Assert.That(entity.DetailsJson, Does.Contain("UserId"));
        Assert.That(entity.DetailsJson, Does.Contain("TargetRole"));
    }

    [Test]
    public async Task RecordEventAsync_BreakGlassGranted_PersistsWithNormalizedFields()
    {
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString();

        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass access granted",
            TenantId = tenantId,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            CorrelationId = correlationId,
            Operation = "NetworkPolicyOverride",
            SourceIpHash = "abc123hash",
            UserAgentHash = "def456hash"
        };

        var entity = new AuditSecurityEventEntity
        {
            TenantId = tenantId,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            CorrelationId = correlationId,
            Operation = "NetworkPolicyOverride",
            SourceIpHash = "abc123hash",
            UserAgentHash = "def456hash"
        };
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.TenantId, Is.EqualTo(tenantId));
        Assert.That(entity.ActorUserId, Is.EqualTo(actorUserId));
        Assert.That(entity.SubjectUserId, Is.EqualTo(subjectUserId));
        Assert.That(entity.CorrelationId, Is.EqualTo(correlationId));
        Assert.That(entity.Operation, Is.EqualTo("NetworkPolicyOverride"));
        Assert.That(entity.SourceIpHash, Is.EqualTo("abc123hash"));
        Assert.That(entity.UserAgentHash, Is.EqualTo("def456hash"));
        _mockRepository.Verify(r => r.AddAsync(entity, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RecordEventAsync_HashOnlyBreakGlass_DoesNotStampRawIpAddress()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass with hash-only metadata",
            SourceIpHash = "sha256hashofip",
            IpAddress = null
        };

        var entity = new AuditSecurityEventEntity
        {
            SourceIpHash = "sha256hashofip"
        };
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        _auditContext.IpAddress = "10.0.0.100";

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.IpAddress, Is.Null,
            "When SourceIpHash is set and IpAddress is null, raw IP should not be stamped from auditContext");
        Assert.That(entity.SourceIpHash, Is.EqualTo("sha256hashofip"));
    }

    [Test]
    public async Task RecordEventAsync_HashOnlyButExplicitIpProvided_UsesProvidedIp()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassAttempt,
            Severity = SecurityEventSeverity.Medium,
            Message = "Break-glass attempt with explicit IP",
            SourceIpHash = "sha256hashofip",
            IpAddress = "192.168.1.1"
        };

        var entity = new AuditSecurityEventEntity
        {
            SourceIpHash = "sha256hashofip"
        };
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.IpAddress, Is.EqualTo("192.168.1.1"),
            "When IpAddress is explicitly provided, it should be used regardless of SourceIpHash");
    }

    [Test]
    public async Task RecordEventAsync_NoSourceIpHash_StampsFromAuditContext()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.UnauthorizedAccess,
            Severity = SecurityEventSeverity.High,
            Message = "Standard security event without hash"
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        _auditContext.IpAddress = "10.0.0.200";

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.IpAddress, Is.EqualTo("10.0.0.200"),
            "When SourceIpHash is not set, IpAddress should be stamped from auditContext");
    }

    [Test]
    public void RecordEventAsync_AddAsyncFailure_PropagatesException()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Critical event that must persist"
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockRepository.Setup(r => r.AddAsync(entity, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Database connection failed"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordEventAsync(inputDto));
    }

    [Test]
    public void RecordEventAsync_SaveChangesAsyncFailure_PropagatesException()
    {
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassConsumed,
            Severity = SecurityEventSeverity.Critical,
            Message = "Critical event that must persist"
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockRepository.Setup(static r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Save failed"));

        Assert.ThrowsAsync<InvalidOperationException>(() => _service.RecordEventAsync(inputDto));
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
    public async Task RecordEventAsync_AllBreakGlassEventTypes_MapAndPersist(SecurityEventType eventType)
    {
        var inputDto = new SecurityEventDto
        {
            EventType = eventType,
            Severity = SecurityEventSeverity.High,
            Message = $"Test {eventType}"
        };

        var entity = new AuditSecurityEventEntity { EventType = eventType };
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto { EventType = eventType });

        var result = await _service.RecordEventAsync(inputDto);

        Assert.That(result.EventType, Is.EqualTo(eventType));
        _mockRepository.Verify(r => r.AddAsync(It.Is<AuditSecurityEventEntity>(e => e.EventType == eventType),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task RecordEventAsync_LargeDetails_ProducesValidJsonSummary()
    {
        var largeDetails = new Dictionary<string, object?>
        {
            ["key1"] = new string('x', 2000),
            ["key2"] = new string('y', 2000),
            ["key3"] = new string('z', 2000)
        };
        var inputDto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Event with large details",
            Details = largeDetails
        };

        var entity = new AuditSecurityEventEntity();
        _mockMapper.Setup(m => m.Map<AuditSecurityEventEntity>(inputDto)).Returns(entity);
        _mockMapper.Setup(m => m.Map<SecurityEventDto>(entity)).Returns(new SecurityEventDto());

        await _service.RecordEventAsync(inputDto);

        Assert.That(entity.DetailsJson, Is.Not.Null);
        Assert.That(entity.DetailsJson!.Length, Is.LessThanOrEqualTo(4000),
            "DetailsJson should be truncated to max length");

        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(entity.DetailsJson);
        Assert.That(parsed, Is.Not.Null, "Truncated DetailsJson must be valid JSON");
        Assert.That(parsed!.ContainsKey("_truncated"), Is.True);
        Assert.That(parsed.ContainsKey("_originalLength"), Is.True);
        Assert.That(parsed.ContainsKey("_keyCount"), Is.True);
        Assert.That(parsed.ContainsKey("_keys"), Is.True);
    }
}