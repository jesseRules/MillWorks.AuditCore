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
