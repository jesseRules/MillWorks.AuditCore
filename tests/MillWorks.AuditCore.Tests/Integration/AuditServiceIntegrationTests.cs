using Mapster;
using MapsterMapper;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// End-to-end integration tests for AuditService using SQLite.
/// Validates: Service -> Mapster DTO mapping -> Repository -> EF Core -> SQLite
/// </summary>
[TestFixture]
[Category("Integration")]
public class AuditServiceIntegrationTests : SqliteIntegrationFixture
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
    public async Task GetAuditEventById_SeedOneEvent_ReturnsCorrectDto()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var logRepo = new AuditLogRepository(context);
        var service = new AuditService(logRepo, eventRepo, _mapper, NullLogger<AuditService>.Instance);

        var eventId = Guid.NewGuid();
        await context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "User.Created",
            User = "alice@test.com",
            EntityType = "User",
            EntityId = "123",
            Environment = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        // Act
        var dto = await service.GetAuditEventById(eventId);

        // Assert
        Assert.That(dto, Is.Not.Null);
        Assert.That(dto!.EventId, Is.EqualTo(eventId));
        Assert.That(dto.EventType, Is.EqualTo("User.Created"));
        Assert.That(dto.User, Is.EqualTo("alice@test.com"));
        Assert.That(dto.EntityId, Is.EqualTo("123"));
    }

    [Test]
    public async Task GetAuditEvents_Pagination_ReturnsCorrectPage()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var logRepo = new AuditLogRepository(context);
        var service = new AuditService(logRepo, eventRepo, _mapper, NullLogger<AuditService>.Instance);

        for (int i = 0; i < 25; i++)
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Event.Type{i}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i)
            });
        }
        await context.SaveChangesAsync();

        // Act — get first page of 10
        var response = await service.GetAuditEvents(offset: 0, limit: 10);

        // Assert
        Assert.That(response.TotalItems, Is.EqualTo(25));
        Assert.That(response.Items, Has.Count.EqualTo(10));
        Assert.That(response.TotalPages, Is.EqualTo(3));
        Assert.That(response.CurrentPage, Is.EqualTo(1));
    }

    [Test]
    public async Task GetAuditEventById_NonExistent_ReturnsNull()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var logRepo = new AuditLogRepository(context);
        var service = new AuditService(logRepo, eventRepo, _mapper, NullLogger<AuditService>.Instance);

        // Act
        var dto = await service.GetAuditEventById(Guid.NewGuid());

        // Assert
        Assert.That(dto, Is.Null);
    }
}
