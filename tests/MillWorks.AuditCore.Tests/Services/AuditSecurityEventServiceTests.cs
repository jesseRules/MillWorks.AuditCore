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
    private Mock<ILogger<AuditSecurityEventService>> _mockLogger;
    private IConfiguration _configuration;
    private IAuditSecurityEventService _service;

    [SetUp]
    public void Setup()
    {
        _mockRepository = new Mock<ISecurityEventRepository>();
        _auditContext = new AuditContext();
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        var result = await _service.RecordEventAsync(inputDto);

        Assert.That(result, Is.Not.Null);
        Assert.That(result.Message, Is.EqualTo("Unauthorized access attempt"));
        Assert.That(captured, Is.Not.Null);
        _mockRepository.Verify(
            r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()), Times.Once);
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        _auditContext.UserEmail = "admin@example.com";
        _auditContext.IpAddress = "10.0.0.1";

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DetectedAt, Is.Not.EqualTo(default(DateTimeOffset)));
        Assert.That(captured.DetectedBy, Is.EqualTo("admin@example.com"));
        Assert.That(captured.IpAddress, Is.EqualTo("10.0.0.1"));
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DetectedBy, Is.EqualTo("System"));
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

        // SendAlertAsync is called internally for critical events - verify via logger
        _mockLogger.Verify(static x => x.Log(
                LogLevel.Critical,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);

        await _service.RecordEventAsync(inputDto);

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

        // The repository filters by severity server-side; the service must request Critical only.
        _mockRepository.Setup(static r => r.GetBySeverityAndDateRangeAsync(
                SecurityEventSeverity.Critical,
                It.IsAny<DateTimeOffset>(),
                It.IsAny<DateTimeOffset>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditSecurityEventEntity> { criticalEntity });

        var result = (await _service.GetCriticalEventsAsync(24)).ToList();

        Assert.That(result.Count, Is.EqualTo(1));
        // The DTO carries the severity through real mapping, proving "critical only".
        Assert.That(result[0].Severity, Is.EqualTo(SecurityEventSeverity.Critical));
        // Prove the "critical only" guarantee comes from querying Critical severity, not chance.
        _mockRepository.Verify(static r => r.GetBySeverityAndDateRangeAsync(
            SecurityEventSeverity.Critical,
            It.IsAny<DateTimeOffset>(),
            It.IsAny<DateTimeOffset>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void AuditSecurityEventEntity_IsAppendOnly()
    {
        // Security events are immutable facts: recorded once, never updated or deleted.
        // The marker makes AppendOnlyInterceptor reject any post-insert modification/deletion.
        // Operational triage/resolution is owned by MillWorks.Security, not AuditCore — so the
        // service exposes no ResolveEventAsync mutation path.
        Assert.That(new AuditSecurityEventEntity(), Is.InstanceOf<IAppendOnlyEntity>());
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DetailsJson, Is.Not.Null);
        Assert.That(captured.DetailsJson, Does.Contain("UserId"));
        Assert.That(captured.DetailsJson, Does.Contain("TargetRole"));
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.TenantId, Is.EqualTo(tenantId));
        Assert.That(captured.ActorUserId, Is.EqualTo(actorUserId));
        Assert.That(captured.SubjectUserId, Is.EqualTo(subjectUserId));
        Assert.That(captured.CorrelationId, Is.EqualTo(correlationId));
        Assert.That(captured.Operation, Is.EqualTo("NetworkPolicyOverride"));
        Assert.That(captured.SourceIpHash, Is.EqualTo("abc123hash"));
        Assert.That(captured.UserAgentHash, Is.EqualTo("def456hash"));
        _mockRepository.Verify(
            r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()), Times.Once);
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        _auditContext.IpAddress = "10.0.0.100";

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.IpAddress, Is.Null,
            "When SourceIpHash is set and IpAddress is null, raw IP should not be stamped from auditContext");
        Assert.That(captured.SourceIpHash, Is.EqualTo("sha256hashofip"));
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.IpAddress, Is.EqualTo("192.168.1.1"),
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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        _auditContext.IpAddress = "10.0.0.200";

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.IpAddress, Is.EqualTo("10.0.0.200"),
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

        _mockRepository.Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
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

        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

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

        AuditSecurityEventEntity? captured = null;
        _mockRepository
            .Setup(r => r.AddAsync(It.IsAny<AuditSecurityEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditSecurityEventEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync((AuditSecurityEventEntity e, CancellationToken _) => e);

        await _service.RecordEventAsync(inputDto);

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DetailsJson, Is.Not.Null);
        Assert.That(captured.DetailsJson!.Length, Is.LessThanOrEqualTo(4000),
            "DetailsJson should be truncated to max length");

        var parsed = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(captured.DetailsJson);
        Assert.That(parsed, Is.Not.Null, "Truncated DetailsJson must be valid JSON");
        Assert.That(parsed!.ContainsKey("_truncated"), Is.True);
        Assert.That(parsed.ContainsKey("_originalLength"), Is.True);
        Assert.That(parsed.ContainsKey("_keyCount"), Is.True);
        Assert.That(parsed.ContainsKey("_keys"), Is.True);
    }
}
