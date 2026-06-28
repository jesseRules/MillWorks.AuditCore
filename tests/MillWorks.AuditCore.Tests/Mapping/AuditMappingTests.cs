using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Mapping;

[TestFixture]
[Category("Unit")]
public class AuditMappingTests
{
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

        var dto = entity.ToDto();

        Assert.That(dto.EventId, Is.EqualTo(entity.EventId));
        Assert.That(dto.EventType, Is.EqualTo("User.Login"));
        Assert.That(dto.User, Is.EqualTo("admin@example.com"));
        Assert.That(dto.UserEnvName, Is.EqualTo("Production"));
        Assert.That(dto.InsertedDate, Is.EqualTo(entity.InsertedDate));
        Assert.That(dto.JsonData, Is.EqualTo("{\"key\":\"value\"}"));
        Assert.That(dto.EntityId, Is.EqualTo("entity-123"));
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

        var entity = dto.ToEntity();

        Assert.That(entity.EventId, Is.EqualTo(dto.EventId));
        Assert.That(entity.EventType, Is.EqualTo("Data.Export"));
        Assert.That(entity.User, Is.EqualTo("user@example.com"));
    }

    [Test]
    public void AuditEventDto_ToEntity_NullEventId_PreservesGeneratedKey()
    {
        var dto = new AuditEventDto { EventType = "Data.Export", EventId = null };

        var entity = dto.ToEntity();

        // EventId has no source value, so the entity's constructor-generated key is preserved
        // (not overwritten with Guid.Empty).
        Assert.That(entity.EventId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test]
    public void AuditEventEntity_ToDto_IgnoresDataProperty()
    {
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            JsonData = "{\"test\":true}"
        };

        var dto = entity.ToDto();

        // Data (parsed response) is never populated by the mapping.
        Assert.That(dto.Data, Is.Null);
    }

    [Test]
    public void AuditEventEntity_ToDto_MapsIntegrityNavigationOneLevel()
    {
        var entity = new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            AuditIntegrity = new AuditIntegrityEntity
            {
                EventId = Guid.NewGuid(),
                EventHash = new string('A', 44),
                Checksum = new string('C', 44),
                SequenceNumber = 7
            }
        };
        // Simulate EF relationship-fixup back-reference (would otherwise be a cycle).
        entity.AuditIntegrity.AuditEvent = entity;

        var dto = entity.ToDto();

        Assert.That(dto.AuditIntegrity, Is.Not.Null);
        Assert.That(dto.AuditIntegrity!.SequenceNumber, Is.EqualTo(7));
        // The cycle is broken: the nested integrity DTO does not carry the event back-reference.
        Assert.That(dto.AuditIntegrity.AuditEvent, Is.Null);
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

        var dto = entity.ToDto();

        Assert.That(dto.EntityName, Is.EqualTo("Customer"));
        Assert.That(dto.EntityId, Is.EqualTo(entity.EntityId));
        Assert.That(dto.Action, Is.EqualTo(AuditAction.Updated));
        Assert.That(dto.PropertyName, Is.EqualTo("Email"));
        Assert.That(dto.OldValue, Is.EqualTo("old@test.com"));
        Assert.That(dto.NewValue, Is.EqualTo("new@test.com"));
        Assert.That(dto.CorrelationId, Is.EqualTo("corr-123"));
        Assert.That(dto.IpAddress, Is.EqualTo("10.0.0.1"));
        Assert.That(dto.CreatedAt, Is.EqualTo(entity.CreatedAt));
        Assert.That(dto.CreatedById, Is.EqualTo(entity.CreatedById));
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

        var entity = dto.ToEntity();

        Assert.That(entity.EntityName, Is.EqualTo("Order"));
        Assert.That(entity.EntityId, Is.EqualTo(dto.EntityId));
        Assert.That(entity.Action, Is.EqualTo(AuditAction.Created));
    }

    [Test]
    public void AuditIntegrityEntity_MapsTo_AuditIntegrityDto()
    {
        var entity = new AuditIntegrityEntity
        {
            EventId = Guid.NewGuid(),
            EventHash = new string('A', 44),
            PreviousEventHash = new string('B', 44),
            Checksum = new string('C', 44),
            TrustedTimestamp = DateTimeOffset.UtcNow,
            SequenceNumber = 42,
            AlgorithmVersion = 1
        };

        var dto = entity.ToDto();

        Assert.That(dto.EventId, Is.EqualTo(entity.EventId));
        Assert.That(dto.EventHash, Is.EqualTo(entity.EventHash));
        Assert.That(dto.PreviousEventHash, Is.EqualTo(entity.PreviousEventHash));
        Assert.That(dto.Checksum, Is.EqualTo(entity.Checksum));
        Assert.That(dto.SequenceNumber, Is.EqualTo(42));
        Assert.That(dto.AlgorithmVersion, Is.EqualTo(1));
    }

    [Test]
    public void AuditIntegrityDto_MapsTo_AuditIntegrityEntity_DefaultsPreservedWhenNull()
    {
        var dto = new AuditIntegrityDto
        {
            EventId = Guid.NewGuid(),
            EventHash = new string('A', 44),
            Checksum = new string('C', 44),
            SequenceNumber = 5,
            TrustedTimestamp = DateTimeOffset.UtcNow
            // AlgorithmVersion left null
        };

        var entity = dto.ToEntity();

        Assert.That(entity.EventId, Is.EqualTo(dto.EventId));
        Assert.That(entity.EventHash, Is.EqualTo(dto.EventHash));
        // Null source preserves the entity's initializer default of 1.
        Assert.That(entity.AlgorithmVersion, Is.EqualTo(1));
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
            IpAddress = "192.168.1.1"
        };

        var dto = entity.ToDto();

        Assert.That(dto.EventType, Is.EqualTo(SecurityEventType.UnauthorizedAccess));
        Assert.That(dto.Severity, Is.EqualTo(SecurityEventSeverity.High));
        Assert.That(dto.Message, Is.EqualTo("Access denied"));
        Assert.That(dto.IpAddress, Is.EqualTo("192.168.1.1"));
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

        var entity = dto.ToEntity();

        Assert.That(entity.EventType, Is.EqualTo(SecurityEventType.AuditTamperAlert));
        Assert.That(entity.Severity, Is.EqualTo(SecurityEventSeverity.Critical));
        Assert.That(entity.Message, Is.EqualTo("Tamper detected"));
        // DetailsJson is intentionally not set by the mapping (the recording path owns it).
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

        var dto = entity.ToDto();

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

        var dto = entity.ToDto();

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

        var dto = entity.ToDto();

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

        var dto = entity.ToDto();

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

        var dto = entity.ToDto();

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

        var dto = entity.ToDto();

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

        var entity = dto.ToEntity();

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

        var entity = dto.ToEntity();

        Assert.That(entity.EventType, Is.EqualTo(eventType));

        var roundTripped = entity.ToDto();
        Assert.That(roundTripped.EventType, Is.EqualTo(eventType));
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
        Assert.DoesNotThrow(() => dto = entity.ToDto());
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.EventType, Is.Null);
        Assert.That(dto.User, Is.Null);
    }
}
