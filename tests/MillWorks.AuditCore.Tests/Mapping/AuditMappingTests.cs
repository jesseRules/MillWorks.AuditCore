using Mapster;
using MapsterMapper;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Mapping;

[TestFixture]
[Category("Unit")]
public class AuditMappingTests
{
    /// <summary>
    /// Mapper instance configured with AuditMappingConfiguration for testing all mappings between entities and DTOs.
    /// </summary>
    private IMapper _mapper;

    [SetUp]
    public void Setup()
    {
        var config = new TypeAdapterConfig();
        config.Apply(new AuditMappingConfiguration());
        _mapper = new Mapper(config);
    }

    [Test]
    public void AuditEventEntity_MapsTo_AuditEventDto()
    {
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "User.Login",
            User = "admin@example.com",
            UserEnvName = "Production",
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{\"key\":\"value\"}",
            EntityId = "entity-123"
        };

        var dto = _mapper.Map<AuditEventDto>(entity);

        Assert.That(dto.EventId, Is.EqualTo(entity.EventId));
        Assert.That(dto.EventType, Is.EqualTo("User.Login"));
        Assert.That(dto.User, Is.EqualTo("admin@example.com"));
        Assert.That(dto.UserEnvName, Is.EqualTo("Production"));
        Assert.That(dto.InsertedDate, Is.EqualTo(entity.InsertedDate));
        Assert.That(dto.JsonData, Is.EqualTo("{\"key\":\"value\"}"));
    }

    [Test]
    public void AuditEventDto_MapsTo_AuditEventEntity()
    {
        var dto = new AuditEventDto
        {
            EventId = Guid.NewGuid(),
            EventType = "Data.Export",
            User = "user@example.com",
            InsertedDate = DateTimeOffset.UtcNow
        };

        var entity = _mapper.Map<AuditEventEntity>(dto);

        Assert.That(entity.EventType, Is.EqualTo("Data.Export"));
        Assert.That(entity.User, Is.EqualTo("user@example.com"));
    }

    [Test]
    public void AuditEventEntity_ToDto_IgnoresDataProperty()
    {
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            JsonData = "{\"test\":true}"
        };

        var dto = _mapper.Map<AuditEventDto>(entity);

        // Data (parsed response) should be ignored per mapping config
        Assert.That(dto.Data, Is.Null);
    }

    [Test]
    public void AuditLogEntity_MapsTo_AuditLogDto()
    {
        var entity = new AuditLogEntity
        {
            EntityName = "Customer",
            EntityId = Guid.NewGuid(),
            Action = AuditAction.Updated,
            PropertyName = "Email",
            OldValue = "old@test.com",
            NewValue = "new@test.com",
            Description = "Email changed",
            CorrelationId = "corr-123",
            IpAddress = "10.0.0.1",
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedById = Guid.NewGuid()
        };

        var dto = _mapper.Map<AuditLogDto>(entity);

        Assert.That(dto.EntityName, Is.EqualTo("Customer"));
        Assert.That(dto.EntityId, Is.EqualTo(entity.EntityId));
        Assert.That(dto.Action, Is.EqualTo(AuditAction.Updated));
        Assert.That(dto.PropertyName, Is.EqualTo("Email"));
        Assert.That(dto.OldValue, Is.EqualTo("old@test.com"));
        Assert.That(dto.NewValue, Is.EqualTo("new@test.com"));
        Assert.That(dto.CorrelationId, Is.EqualTo("corr-123"));
    }

    [Test]
    public void AuditLogDto_MapsTo_AuditLogEntity()
    {
        var dto = new AuditLogDto
        {
            EntityName = "Order",
            EntityId = Guid.NewGuid(),
            Action = AuditAction.Created,
            Description = "New order",
            CreatedById = Guid.NewGuid()
        };

        var entity = _mapper.Map<AuditLogEntity>(dto);

        Assert.That(entity.EntityName, Is.EqualTo("Order"));
        Assert.That(entity.Action, Is.EqualTo(AuditAction.Created));
    }

    [Test]
    public void AuditIntegrityEntity_MapsTo_AuditIntegrityDto()
    {
        var entity = new AuditIntegrityEntity
        {
            EventId = Guid.NewGuid(),
            EventHash = new string('A', 64),
            PreviousEventHash = new string('B', 64),
            Checksum = new string('C', 44),
            TrustedTimestamp = DateTimeOffset.UtcNow,
            SequenceNumber = 42,
            AlgorithmVersion = 1
        };

        var dto = _mapper.Map<AuditIntegrityDto>(entity);

        Assert.That(dto.EventId, Is.EqualTo(entity.EventId));
    }

    [Test]
    public void SecurityEventEntity_MapsTo_SecurityEventDto()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.UnauthorizedAccess,
            Severity = SecurityEventSeverity.High,
            Message = "Access denied",
            DetectedAt = DateTimeOffset.UtcNow,
            DetectedBy = "System",
            Status = SecurityEventStatus.Open,
            IpAddress = "192.168.1.1"
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.EventType, Is.EqualTo(SecurityEventType.UnauthorizedAccess));
        Assert.That(dto.Severity, Is.EqualTo(SecurityEventSeverity.High));
        Assert.That(dto.Message, Is.EqualTo("Access denied"));
        Assert.That(dto.Status, Is.EqualTo(SecurityEventStatus.Open));
    }

    [Test]
    public void SecurityEventDto_MapsTo_SecurityEventEntity()
    {
        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.AuditTamperAlert,
            Severity = SecurityEventSeverity.Critical,
            Message = "Tamper detected"
        };

        var entity = _mapper.Map<AuditSecurityEventEntity>(dto);

        Assert.That(entity.EventType, Is.EqualTo(SecurityEventType.AuditTamperAlert));
        Assert.That(entity.Severity, Is.EqualTo(SecurityEventSeverity.Critical));
        Assert.That(entity.Message, Is.EqualTo("Tamper detected"));
        // DetailsJson should be ignored per mapping config
        Assert.That(entity.DetailsJson, Is.Null);
    }

    [Test]
    public void SecurityEventEntity_ToDto_ParsesDetailsJsonToDetails()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass granted",
            DetailsJson = """{"GrantId":"grant-123","PolicyAfterHash":"abc456","GrantTtlSeconds":3600}"""
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.Details, Is.Not.Null);
        Assert.That(dto.Details, Has.Count.EqualTo(3));
        Assert.That(dto.Details["GrantId"], Is.EqualTo("grant-123"));
        Assert.That(dto.Details["PolicyAfterHash"], Is.EqualTo("abc456"));
        Assert.That(dto.Details["GrantTtlSeconds"], Is.EqualTo(3600L));
    }

    [Test]
    public void SecurityEventEntity_ToDto_HandlesNullDetailsJson()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassDenied,
            Severity = SecurityEventSeverity.Medium,
            Message = "Access denied",
            DetailsJson = null
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.Details, Is.Not.Null);
        Assert.That(dto.Details, Is.Empty);
    }

    [Test]
    public void SecurityEventEntity_ToDto_HandlesMalformedDetailsJson()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassChallengeFailed,
            Severity = SecurityEventSeverity.High,
            Message = "Challenge failed",
            DetailsJson = "this is not valid json {"
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.Details, Is.Not.Null);
        Assert.That(dto.Details, Is.Empty);
    }

    [Test]
    public void SecurityEventEntity_ToDto_HandlesEmptyDetailsJson()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassExpired,
            Severity = SecurityEventSeverity.Low,
            Message = "Grant expired",
            DetailsJson = ""
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.Details, Is.Not.Null);
        Assert.That(dto.Details, Is.Empty);
    }

    [Test]
    public void SecurityEventEntity_ToDto_ParsesNestedDetailsJson()
    {
        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassPolicyChanged,
            Severity = SecurityEventSeverity.Critical,
            Message = "Policy changed",
            DetailsJson = """{"Policy":{"AllowedCountries":["US","CA"],"MaxGrants":5},"ChangedBy":"admin"}"""
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.Details, Has.Count.EqualTo(2));
        Assert.That(dto.Details["ChangedBy"], Is.EqualTo("admin"));
        Assert.That(dto.Details["Policy"], Is.TypeOf<Dictionary<string, object?>>());

        var policy = (Dictionary<string, object?>)dto.Details["Policy"]!;
        Assert.That(policy["MaxGrants"], Is.EqualTo(5L));
    }

    [Test]
    public void SecurityEventEntity_NormalizedFields_RoundTrip()
    {
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();
        var subjectUserId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString();

        var entity = new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.BreakGlassGranted,
            Severity = SecurityEventSeverity.Critical,
            Message = "Break-glass granted",
            TenantId = tenantId,
            ActorUserId = actorUserId,
            SubjectUserId = subjectUserId,
            CorrelationId = correlationId,
            Operation = "NetworkPolicyOverride",
            SourceIpHash = "sha256ipaddress",
            UserAgentHash = "sha256useragent"
        };

        var dto = _mapper.Map<SecurityEventDto>(entity);

        Assert.That(dto.TenantId, Is.EqualTo(tenantId));
        Assert.That(dto.ActorUserId, Is.EqualTo(actorUserId));
        Assert.That(dto.SubjectUserId, Is.EqualTo(subjectUserId));
        Assert.That(dto.CorrelationId, Is.EqualTo(correlationId));
        Assert.That(dto.Operation, Is.EqualTo("NetworkPolicyOverride"));
        Assert.That(dto.SourceIpHash, Is.EqualTo("sha256ipaddress"));
        Assert.That(dto.UserAgentHash, Is.EqualTo("sha256useragent"));
    }

    [Test]
    public void SecurityEventDto_NormalizedFields_MapsToEntity()
    {
        var tenantId = Guid.NewGuid();
        var actorUserId = Guid.NewGuid();

        var dto = new SecurityEventDto
        {
            EventType = SecurityEventType.BreakGlassAttempt,
            Severity = SecurityEventSeverity.Medium,
            Message = "Attempt",
            TenantId = tenantId,
            ActorUserId = actorUserId,
            Operation = "MfaBypass"
        };

        var entity = _mapper.Map<AuditSecurityEventEntity>(dto);

        Assert.That(entity.TenantId, Is.EqualTo(tenantId));
        Assert.That(entity.ActorUserId, Is.EqualTo(actorUserId));
        Assert.That(entity.Operation, Is.EqualTo("MfaBypass"));
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
    public void BreakGlassEventType_MapsCorrectly(SecurityEventType eventType)
    {
        var dto = new SecurityEventDto
        {
            EventType = eventType,
            Severity = SecurityEventSeverity.High,
            Message = $"Test {eventType}"
        };

        var entity = _mapper.Map<AuditSecurityEventEntity>(dto);

        Assert.That(entity.EventType, Is.EqualTo(eventType));

        var roundTripped = _mapper.Map<SecurityEventDto>(entity);
        Assert.That(roundTripped.EventType, Is.EqualTo(eventType));
    }

    [Test]
    public void AllMappings_DoNotThrow()
    {
        // Verify the entire configuration is valid
        var config = new TypeAdapterConfig();
        config.Apply(new AuditMappingConfiguration());

        Assert.DoesNotThrow(() => config.Compile());
    }

    [Test]
    public void Mapping_NullSourceProperties_HandledGracefully()
    {
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = null,
            User = null,
            JsonData = null,
            InsertedDate = null
        };

        AuditEventDto? dto = null;
        Assert.DoesNotThrow(() => dto = _mapper.Map<AuditEventDto>(entity));
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.EventType, Is.Null);
        Assert.That(dto.User, Is.Null);
    }

    [Test]
    public void AuditEntry_MapsTo_AuditEventEntity_WithFormattedEventType()
    {
        var entry = new AuditEntry
        {
            EntityName = "Customer",
            Action = "Created",
            UserId = Guid.NewGuid(),
            AspNetUserId = "aspnet-123",
            KeyValues = new Dictionary<string, object?> { { "Id", Guid.NewGuid() } }
        };

        var entity = _mapper.Map<AuditEventEntity>(entry);

        Assert.That(entity.EventType, Is.EqualTo("Customer.Created"));
        Assert.That(entity.EntityType, Is.EqualTo("Customer"));
        Assert.That(entity.Action, Is.EqualTo("Created"));
        Assert.That(entity.UserId, Is.EqualTo(entry.UserId));
        Assert.That(entity.AspNetUserId, Is.EqualTo("aspnet-123"));
    }

    [Test]
    public void AuditEntry_MapsTo_AuditEventEntity_SetsInsertedDate()
    {
        var before = DateTimeOffset.UtcNow;

        var entry = new AuditEntry
        {
            EntityName = "Order",
            Action = "Updated"
        };

        var entity = _mapper.Map<AuditEventEntity>(entry);

        Assert.That(entity.InsertedDate, Is.GreaterThanOrEqualTo(before));
    }

    [Test]
    public void AuditEntry_MapsTo_AuditEventEntity_ExtractsEntityId()
    {
        var entityId = Guid.NewGuid();
        var entry = new AuditEntry
        {
            EntityName = "Product",
            Action = "Deleted",
            KeyValues = new Dictionary<string, object?> { { "Id", entityId } }
        };

        var entity = _mapper.Map<AuditEventEntity>(entry);

        Assert.That(entity.EntityId, Is.EqualTo(entityId.ToString()));
    }
}
