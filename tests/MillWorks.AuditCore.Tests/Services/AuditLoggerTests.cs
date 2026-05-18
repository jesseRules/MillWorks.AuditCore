using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// AuditLogger tests
/// </summary>
[TestFixture]
public class AuditLoggerTests
{
    /// <summary>
    /// Mock logger
    /// </summary>
    private Mock<ILogger<AuditLogger>> _mockLogger;

    /// <summary>
    /// Mock audit event factory
    /// </summary>
    private Mock<IAuditEventFactory> _mockEventFactory;

    /// <summary>
    /// Mock audit event repository
    /// </summary>
    private Mock<IAuditEventRepository> _mockAuditEventRepository;

    /// <summary>
    /// Mock tamper detection service
    /// </summary>
    private Mock<ITamperDetectionService> _mockTamperDetectionService;

    /// <summary>
    /// Mock audit context
    /// </summary>
    private Mock<IAuditContext> _mockAuditContext;

    /// <summary>
    /// In-memory DbContext for work item operations
    /// </summary>
    private AuditDbContext _dbContext;

    /// <summary>
    /// AuditLogger instance under test
    /// </summary>
    private AuditLogger _auditLogger;

    /// <summary>
    /// Setup initializes before each test
    /// </summary>
    [SetUp]
    public void Setup()
    {
        _mockLogger = new Mock<ILogger<AuditLogger>>();
        _mockEventFactory = new Mock<IAuditEventFactory>();
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockTamperDetectionService = new Mock<ITamperDetectionService>();
        _mockAuditContext = new Mock<IAuditContext>();

        var dbOptions = new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _dbContext = new AuditDbContext(dbOptions);

        // LogAsync wraps event + integrity in ExecuteInTransactionAsync when tamper detection is enabled.
        // The mock must invoke the lambda so the inner logic actually executes.
        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        _auditLogger = new AuditLogger(
            _mockLogger.Object,
            _mockEventFactory.Object,
            _mockAuditEventRepository.Object,
            _dbContext,
            _mockAuditContext.Object,
            new PassThroughAuditFieldRedactor(),
            _mockTamperDetectionService.Object);
    }

    /// <summary>
    /// Cleanup after each test
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    /// <summary>
    /// LogAsync_WithValidAuditEvent_SavesEventSuccessfully
    /// </summary>
    [Test]
    public async Task LogAsync_WithValidAuditEvent_SavesEventSuccessfully()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow
        };

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto { EventId = auditEvent.EventId });

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        _mockAuditEventRepository.Verify(static x => x.AddAsync(
            It.IsAny<AuditEventEntity>(),
            It.IsAny<CancellationToken>()), Times.Once);

        _mockAuditEventRepository.Verify(static x => x.SaveChangesAsync(
            It.IsAny<CancellationToken>()), Times.Once);

        _mockTamperDetectionService.Verify(static x => x.CreateIntegrityRecordAsync(
            It.IsAny<AuditIntegrityDto>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// LogAsync_WithEventTypeAndData_CreatesEventAndLogs
    /// </summary>
    [Test]
    public async Task LogAsync_WithEventTypeAndData_CreatesEventAndLogs()
    {
        // Arrange
        var eventType = "User.Login";
        var data = new { UserId = Guid.NewGuid(), IpAddress = "192.168.1.1" };
        var expectedEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            StartDate = DateTimeOffset.UtcNow,
            Target = new AuditTarget { New = data }
        };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, data, It.IsAny<string>()))
            .Returns(expectedEvent);

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(eventType, data);

        // Assert
        _mockEventFactory.Verify(x => x.CreateEvent(eventType, data, It.IsAny<string>()), Times.Once);
        _mockAuditEventRepository.Verify(static x => x.AddAsync(
            It.IsAny<AuditEventEntity>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// BeginOperationAsync_CreatesOperationAndReturnsId
    /// </summary>
    [Test]
    public async Task BeginOperationAsync_CreatesOperationAndReturnsId()
    {
        // Arrange
        var operationType = "DataProcessing";
        var metadata = new { BatchSize = 100 };
        var expectedOperationId = Guid.NewGuid();
        var expectedEvent = new AuditEvent
        {
            EventId = expectedOperationId,
            EventType = $"{operationType}.Started",
            CustomFields = new Dictionary<string, object?>
            {
                ["OperationType"] = operationType,
                ["Status"] = "Started"
            }
        };

        _mockEventFactory
            .Setup(x => x.CreateOperationEvent(operationType, It.IsAny<Guid>(), "Started", metadata))
            .Returns(expectedEvent);

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        var operationId = await _auditLogger.BeginOperationAsync(operationType, metadata);

        // Assert
        Assert.That(operationId, Is.Not.EqualTo(Guid.Empty));
        _mockEventFactory.Verify(x => x.CreateOperationEvent(
            operationType,
            It.IsAny<Guid>(),
            "Started",
            metadata), Times.Once);
    }

    /// <summary>
    /// EndOperationAsync_WithSuccessfulOperation_CreatesNewCompletionEvent
    /// </summary>
    [Test]
    public async Task EndOperationAsync_WithSuccessfulOperation_CreatesNewCompletionEvent()
    {
        // Arrange
        var operationType = "DataProcessing";
        var metadata = new { BatchSize = 100 };
        var result = new { ProcessedRecords = 100 };

        // Create start event
        var startEvent = new AuditEvent
        {
            EventType = $"{operationType}.Started",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["OperationType"] = operationType,
                ["Status"] = "Started"
            }
        };

        _mockEventFactory
            .Setup(x => x.CreateOperationEvent(operationType, It.IsAny<Guid>(), "Started", metadata))
            .Returns(startEvent);

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditIntegrityDto dto, CancellationToken _) => dto);

        // Begin the operation
        var actualOperationId = await _auditLogger.BeginOperationAsync(operationType, metadata);

        // Reset invocation counts after begin
        _mockAuditEventRepository.Invocations.Clear();

        // Setup completion event — must be a NEW event with a different EventId
        var completionEvent = new AuditEvent
        {
            EventType = $"{operationType}.Completed",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["OperationType"] = operationType,
                ["Status"] = "Completed"
            }
        };

        _mockEventFactory
            .Setup(x => x.CreateOperationEvent(operationType, actualOperationId, "Completed", result))
            .Returns(completionEvent);

        // Act
        await _auditLogger.EndOperationAsync(actualOperationId, true, result);

        // Assert — a new completion event was created via the factory (not the start event reused)
        _mockEventFactory.Verify(x => x.CreateOperationEvent(
            operationType,
            actualOperationId,
            "Completed",
            result), Times.Once);

        _mockAuditEventRepository.Verify(static x => x.AddAsync(
            It.IsAny<AuditEventEntity>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// CreateScope_ReturnsCustomAuditScope
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task CreateScope_ReturnsCustomAuditScope()
    {
        // Arrange
        var eventType = "Test.Scope";
        var target = new { Data = "test" };
        var expectedEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            Target = new AuditTarget { New = target }
        };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, target, It.IsAny<string>()))
            .Returns(expectedEvent);

        // Act
        var scope = _auditLogger.CreateScope(eventType, target);

        // Assert
        Assert.That(scope, Is.Not.Null);
        Assert.That(scope, Is.InstanceOf<ICustomAuditScope>());
        Assert.That(scope.Event, Is.Not.Null);
        Assert.That(scope.Event.EventType, Is.EqualTo(eventType));
        return Task.CompletedTask;
    }

    /// <summary>
    /// LogAsync_WhenRepositoryThrowsException_LogsErrorAndThrows
    /// </summary>
    [Test]
    public void LogAsync_WhenRepositoryThrowsException_LogsErrorAndThrows()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Database error"));

        // Act & Assert
        Assert.ThrowsAsync<Exception>(async () => await _auditLogger.LogAsync(auditEvent));

        _mockLogger.Verify(static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => true),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()!),
            Times.Once);
    }

    /// <summary>
    /// LogAsync_WhenTamperDetectionFails_RollsBackTransactionAndThrows
    /// With the transaction wrapping event + integrity (O1 fix), a tamper detection
    /// failure now rolls back both inserts atomically and re-throws so
    /// ResilientAuditLogger can retry the entire operation.
    /// </summary>
    [Test]
    public void LogAsync_WhenTamperDetectionFails_RollsBackTransactionAndThrows()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event"
        };

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Tamper detection failed"));

        // Act & Assert — tamper detection failure now propagates (transaction rolled back)
        Assert.ThrowsAsync<Exception>(async () => await _auditLogger.LogAsync(auditEvent));
    }

    /// <summary>
    /// CustomAuditScope_WhenDisposedAsync_SavesEventAutomatically
    /// </summary>
    /// <returns></returns>
    [Test]
    public async Task CustomAuditScope_WhenDisposedAsync_SavesEventAutomatically()
    {
        // Arrange
        var eventType = "Test.Scope";
        var expectedEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType
        };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, null, It.IsAny<string>()))
            .Returns(expectedEvent);

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act - async Dispose saves the event
        await using (var scope = _auditLogger.CreateScope(eventType))
        {
            scope.SetCustomField("TestField", "TestValue");
        } // DisposeAsync called here

        // Assert
        _mockAuditEventRepository.Verify(static x => x.AddAsync(
            It.IsAny<AuditEventEntity>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    #region ConvertToEntity mapping tests

    /// <summary>
    /// Verifies that UserId in CustomFields is mapped to entity.UserId
    /// </summary>
    [Test]
    public async Task LogAsync_WithUserIdInCustomFields_MapsUserIdToEntity()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?> { ["UserId"] = userId }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.UserId, Is.EqualTo(userId));
    }

    /// <summary>
    /// Verifies that TenantId in CustomFields is mapped to entity.TenantId
    /// </summary>
    [Test]
    public async Task LogAsync_WithTenantIdInCustomFields_MapsTenantIdToEntity()
    {
        // Arrange
        var tenantId = Guid.NewGuid();
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?> { ["TenantId"] = tenantId }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.TenantId, Is.EqualTo(tenantId));
    }

    /// <summary>
    /// Verifies that EntityType and EntityId in CustomFields are mapped correctly
    /// </summary>
    [Test]
    public async Task LogAsync_WithEntityTypeAndEntityId_MapsToEntity()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["EntityType"] = "Order",
                ["EntityId"] = "12345"
            }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.EntityType, Is.EqualTo("Order"));
        Assert.That(capturedEntity.EntityId, Is.EqualTo("12345"));
    }

    /// <summary>
    /// Verifies that IpAddress, UserAgent, RequestPath, RequestMethod are all mapped
    /// </summary>
    [Test]
    public async Task LogAsync_WithHttpContextFields_MapsIpUserAgentRequestPathMethod()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["IpAddress"] = "192.168.1.100",
                ["UserAgent"] = "Mozilla/5.0",
                ["RequestPath"] = "/api/orders",
                ["RequestMethod"] = "POST"
            }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IpAddress, Is.EqualTo("192.168.1.100"));
        Assert.That(capturedEntity.UserAgent, Is.EqualTo("Mozilla/5.0"));
        Assert.That(capturedEntity.RequestPath, Is.EqualTo("/api/orders"));
        Assert.That(capturedEntity.RequestMethod, Is.EqualTo("POST"));
    }

    /// <summary>
    /// Verifies that unmapped custom fields are serialized to AdditionalData
    /// </summary>
    [Test]
    public async Task LogAsync_WithUnmappedCustomFields_SerializesToAdditionalData()
    {
        // Arrange
        var userId = Guid.NewGuid();
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["UserId"] = userId,
                ["CustomMetric"] = 42
            }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.AdditionalData, Is.Not.Null);
        Assert.That(capturedEntity.AdditionalData, Does.Contain("CustomMetric"));
        Assert.That(capturedEntity.AdditionalData, Does.Not.Contain("UserId"));
    }

    /// <summary>
    /// Verifies that when all custom fields are mapped, AdditionalData is null
    /// </summary>
    [Test]
    public async Task LogAsync_WithOnlyMappedFields_AdditionalDataIsNull()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>
            {
                ["EntityType"] = "Order",
                ["EntityId"] = "123",
                ["Action"] = "Created"
            }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.AdditionalData, Is.Null);
    }

    /// <summary>
    /// Verifies that JsonData contains core event properties
    /// </summary>
    [Test]
    public async Task LogAsync_SerializesFullEventToJsonData()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow,
            Target = new AuditTarget { New = new { Foo = "Bar" } },
            CustomFields = new Dictionary<string, object?> { ["Metric"] = 99 }
        };

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(auditEvent);

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.JsonData, Is.Not.Null);
        Assert.That(capturedEntity.JsonData, Does.Contain(auditEvent.EventId.ToString()));
        Assert.That(capturedEntity.JsonData, Does.Contain("Test.Event"));
        Assert.That(capturedEntity.JsonData, Does.Contain("Metric"));
    }

    #endregion

    #region Idempotency & duplicate handling tests

    /// <summary>
    /// Verifies that duplicate key exceptions are treated as success (idempotent)
    /// instead of a pre-check query, the DB unique constraint handles duplicates
    /// </summary>
    [Test]
    public async Task LogAsync_WhenDuplicateKeyOnSave_TreatsAsSuccessWithoutPreCheck()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow
        };

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        var sqlException = CreateSqlException(2627);
        var dbUpdateException = new DbUpdateException("Duplicate key", sqlException);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbUpdateException);

        // Act - should not throw
        await _auditLogger.LogAsync(auditEvent);

        // Assert - no pre-check query was made
        _mockAuditEventRepository.Verify(static x => x.EventExistsAsync(
            It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Verifies that a duplicate key exception (SqlException 2627) is treated as success
    /// </summary>
    [Test]
    public async Task LogAsync_WhenDuplicateKeyException_TreatsAsSuccess()
    {
        // Arrange
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            StartDate = DateTimeOffset.UtcNow
        };

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        // Create a DbUpdateException with a SqlException inner exception (number 2627)
        var sqlException = CreateSqlException(2627);
        var dbUpdateException = new DbUpdateException("Duplicate key", sqlException);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(dbUpdateException);

        // Act - should not throw
        await _auditLogger.LogAsync(auditEvent);

        // Assert - logged debug message, no error
        _mockLogger.Verify(static x => x.Log(
                LogLevel.Debug,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Duplicate key")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()!),
            Times.Once);
    }

    #endregion

    #region EndOperation edge case tests

    /// <summary>
    /// Verifies that EndOperationAsync with unknown scope logs as UnknownOperation
    /// </summary>
    [Test]
    public async Task EndOperationAsync_WhenScopeNotFound_LogsSeparateEventAsUnknownOperation()
    {
        // Arrange
        var randomOperationId = Guid.NewGuid();
        var unknownEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "UnknownOperation",
            CustomFields = new Dictionary<string, object?>()
        };

        _mockEventFactory
            .Setup(x => x.CreateOperationEvent("UnknownOperation", randomOperationId, "Completed", null))
            .Returns(unknownEvent);

        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.EndOperationAsync(randomOperationId);

        // Assert
        _mockEventFactory.Verify(x => x.CreateOperationEvent(
            "UnknownOperation",
            randomOperationId,
            "Completed",
            It.IsAny<object>()), Times.Once);

        _mockLogger.Verify(static x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("scope not found")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception, string>>()!),
            Times.Once);
    }

    #endregion

    #region Message overload test

    /// <summary>
    /// Verifies that LogAsync message overload sets Message in CustomFields
    /// </summary>
    [Test]
    public async Task LogAsync_WithMessageOverload_SetsMessageInCustomFields()
    {
        // Arrange
        var eventType = "Test.Event";
        var message = "Something important happened";
        var data = new Dictionary<string, object?> { ["Key1"] = "Value1" };
        var createdEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = eventType,
            StartDate = DateTimeOffset.UtcNow,
            CustomFields = new Dictionary<string, object?>(data)
        };

        _mockEventFactory
            .Setup(x => x.CreateEvent(eventType, data, It.IsAny<string>()))
            .Returns(createdEvent);

        AuditEventEntity? capturedEntity = null;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditEventEntity e, CancellationToken _) => e);

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(static x =>
                x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditIntegrityDto());

        // Act
        await _auditLogger.LogAsync(eventType, message, data, CancellationToken.None);

        // Assert
        Assert.That(createdEvent.CustomFields["Message"], Is.EqualTo(message));
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.JsonData, Does.Contain(message));
    }

    #endregion

    /// <summary>
    /// Helper to create a SqlException via reflection (no public constructor)
    /// </summary>
    private static SqlException CreateSqlException(int number)
    {
        // SqlException has no public constructor; create via SqlError collection
        var errorCollectionCtor = typeof(SqlErrorCollection)
            .GetConstructor(
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, [], null)!;
        var errorCollection = errorCollectionCtor.Invoke([]);

        // Find the SqlError constructor and match parameter types dynamically
        var sqlErrorCtor = typeof(SqlError)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .OrderByDescending(static c => c.GetParameters().Length)
            .First(static c => c.GetParameters().Length >= 8);

        var ctorParams = sqlErrorCtor.GetParameters();
        var args = new object?[ctorParams.Length];
        for (int i = 0; i < ctorParams.Length; i++)
        {
            var paramType = ctorParams[i].ParameterType;
            if (i == 0) // number
                args[i] = Convert.ChangeType(number, paramType);
            else if (paramType == typeof(byte))
                args[i] = (byte)0;
            else if (paramType == typeof(int))
                args[i] = 0;
            else if (paramType == typeof(uint))
                args[i] = (uint)0;
            else if (paramType == typeof(string))
                args[i] = paramType == typeof(string) ? "test" : null;
            else
                args[i] = null;
        }

        var sqlError = sqlErrorCtor.Invoke(args);

        typeof(SqlErrorCollection)
            .GetMethod("Add", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(errorCollection, [sqlError]);

        return (SqlException)typeof(SqlException)
            .GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .First(static c => c.GetParameters().Length >= 4)
            .Invoke(["Duplicate key", (SqlErrorCollection)errorCollection, null, Guid.NewGuid()]);
    }
}
