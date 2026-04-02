using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.TamperDetection;

[TestFixture]
[Category("Unit")]
public class IntegrityReconciliationServiceTests
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AuditApplicationDbContext> _options = null!;
    private ServiceProvider _serviceProvider = null!;
    private Mock<ITamperDetectionService> _mockTamperDetection = null!;
    private Mock<ILogger<IntegrityReconciliationService>> _mockLogger = null!;
    private AuditDiagnostics _diagnostics = null!;
    private IntegrityReconciliationService _service = null!;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        _mockTamperDetection = new Mock<ITamperDetectionService>();
        _mockLogger = new Mock<ILogger<IntegrityReconciliationService>>();
        _diagnostics = new AuditDiagnostics();

        var services = new ServiceCollection();
        services.AddScoped(_ => CreateContext());
        services.AddScoped(_ => _mockTamperDetection.Object);
        _serviceProvider = services.BuildServiceProvider();

        _service = new IntegrityReconciliationService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger.Object,
            _diagnostics);
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _serviceProvider.Dispose();
        _connection.Dispose();
    }

    [Test]
    public async Task ReconcileAsync_NoStalePendingItems_CompletesWithoutSideEffects()
    {
        var eventId = Guid.NewGuid();

        using var context = CreateContext();
        await context.AuditEvents.AddAsync(new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Audit.Event",
            JsonData = "{}",
            InsertedDate = DateTimeOffset.UtcNow
        });
        await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
        {
            EventId = eventId,
            Status = IntegrityStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await context.SaveChangesAsync();

        await InvokeReconcileAsync();

        _mockTamperDetection.Verify(
            static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
        Assert.That(_diagnostics.IntegrityReconciliationSuccessCount, Is.EqualTo(0));
    }

    [Test]
    public async Task ReconcileAsync_WhenIntegrityAlreadyExists_MarksWorkItemAndEventReconciled()
    {
        var eventId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
                IntegrityStatus = IntegrityStatus.Pending
            });

            await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
            {
                EventId = eventId,
                Status = IntegrityStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            await context.AuditIntegrity.AddAsync(new AuditIntegrityEntity
            {
                EventId = eventId,
                EventHash = new string('A', 44),
                Checksum = new string('B', 44),
                HmacSignature = new string('C', 44),
                TrustedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-9),
                AlgorithmVersion = 1,
                SequenceNumber = 1
            });

            await context.SaveChangesAsync();
        }

        await InvokeReconcileAsync();

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();
        var auditEvent = await assertContext.AuditEvents.SingleAsync();

        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Reconciled));
        Assert.That(workItem.CompletedAt, Is.Not.Null);
        Assert.That(auditEvent.IntegrityStatus, Is.EqualTo(IntegrityStatus.Reconciled));
        Assert.That(_diagnostics.IntegrityReconciliationSuccessCount, Is.EqualTo(1));
        _mockTamperDetection.Verify(
            static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ReconcileAsync_WithStalePendingItemWithoutIntegrity_CreatesIntegrityAndMarksReconciled()
    {
        var eventId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{\"value\":1}",
                User = "tester",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
                IntegrityStatus = IntegrityStatus.Pending
            });

            await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
            {
                EventId = eventId,
                Status = IntegrityStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            await context.SaveChangesAsync();
        }

        await InvokeReconcileAsync();

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();
        var auditEvent = await assertContext.AuditEvents.SingleAsync();

        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Reconciled));
        Assert.That(auditEvent.IntegrityStatus, Is.EqualTo(IntegrityStatus.Reconciled));
        Assert.That(_diagnostics.IntegrityReconciliationSuccessCount, Is.EqualTo(1));

        _mockTamperDetection.Verify(x => x.CreateIntegrityRecordAsync(
                It.Is<AuditIntegrityDto>(dto =>
                    dto.EventId == eventId &&
                    dto.EventType == "Audit.Event" &&
                    dto.JsonData == "{\"value\":1}" &&
                    dto.User == "tester"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ReconcileAsync_WhenEventMissing_MarksWorkItemFailed()
    {
        var eventId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10)
            });
            await context.SaveChangesAsync();

            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
            await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"AuditEvents\" WHERE \"EventId\" = {0};",
                eventId);
            await context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO "AuditIntegrityWorkItems" ("Id", "EventId", "Status", "AttemptCount", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {eventId}, {(int)IntegrityStatus.Pending}, {0}, {DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O")});
                """);
            await context.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");
        }

        await InvokeReconcileAsync();

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();
        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Failed));
        Assert.That(workItem.LastError, Is.EqualTo("Audit event no longer exists"));
        Assert.That(workItem.LastAttemptAt, Is.Not.Null);
    }

    [Test]
    public async Task ReconcileAsync_WhenMaxAttemptsExceeded_MarksFailedAndCreatesSecurityEvent()
    {
        var eventId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
                IntegrityStatus = IntegrityStatus.Pending
            });

            await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
            {
                EventId = eventId,
                Status = IntegrityStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
                AttemptCount = 5,
                LastError = "previous failure"
            });
            await context.SaveChangesAsync();
        }

        await InvokeReconcileAsync();

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();
        var auditEvent = await assertContext.AuditEvents.SingleAsync();
        var securityEvent = await assertContext.SecurityEvents.SingleAsync();

        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Failed));
        Assert.That(workItem.LastAttemptAt, Is.Not.Null);
        Assert.That(workItem.LastError, Is.EqualTo("previous failure"));
        Assert.That(auditEvent.IntegrityStatus, Is.EqualTo(IntegrityStatus.Failed));
        Assert.That(securityEvent.RelatedAuditEventId, Is.EqualTo(eventId));
        Assert.That(securityEvent.EventType, Is.EqualTo(SecurityEventType.IntegrityViolation));
        Assert.That(_diagnostics.IntegrityPermanentFailureCount, Is.EqualTo(1));
        _mockTamperDetection.Verify(
            static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task ReconcileAsync_WhenCreateIntegrityFails_IncrementsAttemptAndStoresTrimmedError()
    {
        var eventId = Guid.NewGuid();
        var longMessage = new string('x', 2500);

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
                IntegrityStatus = IntegrityStatus.Pending
            });

            await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
            {
                EventId = eventId,
                Status = IntegrityStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            await context.SaveChangesAsync();
        }

        _mockTamperDetection
            .Setup(static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(longMessage));

        await InvokeReconcileAsync();

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();
        var auditEvent = await assertContext.AuditEvents.SingleAsync();

        Assert.That(workItem.AttemptCount, Is.EqualTo(1));
        Assert.That(workItem.LastAttemptAt, Is.Not.Null);
        Assert.That(workItem.LastError, Has.Length.EqualTo(2000));
        Assert.That(auditEvent.IntegrityStatus, Is.EqualTo(IntegrityStatus.Pending));
        Assert.That(_diagnostics.IntegrityReconciliationFailureCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ReconcileAsync_ConcurrentPasses_OnlyOneInstanceClaimsAndProcessesWorkItem()
    {
        var eventId = Guid.NewGuid();

        using (var context = CreateContext())
        {
            await context.AuditEvents.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Audit.Event",
                JsonData = "{}",
                InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-10),
                IntegrityStatus = IntegrityStatus.Pending
            });

            await context.IntegrityWorkItems.AddAsync(new AuditIntegrityWorkItemEntity
            {
                EventId = eventId,
                Status = IntegrityStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10)
            });

            await context.SaveChangesAsync();
        }

        _mockTamperDetection
            .Setup(static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                await Task.Delay(150);
                return new AuditIntegrityDto();
            });

        var secondDiagnostics = new AuditDiagnostics();
        using var secondService = new IntegrityReconciliationService(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _mockLogger.Object,
            secondDiagnostics);

        var firstRun = InvokeReconcileAsync();
        await Task.Delay(30);
        var secondRun = InvokeReconcileAsync(secondService);
        await Task.WhenAll(firstRun, secondRun);

        using var assertContext = CreateContext();
        var workItem = await assertContext.IntegrityWorkItems.SingleAsync();

        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Reconciled));
        Assert.That(workItem.LeaseOwner, Is.Null);
        Assert.That(workItem.LeaseExpiresAt, Is.Null);
        _mockTamperDetection.Verify(
            static x => x.CreateIntegrityRecordAsync(It.IsAny<AuditIntegrityDto>(), It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.That(_diagnostics.IntegrityReconciliationSuccessCount + secondDiagnostics.IntegrityReconciliationSuccessCount,
            Is.EqualTo(1));
    }

    private AuditApplicationDbContext CreateContext() => new(_options);

    private async Task InvokeReconcileAsync(CancellationToken cancellationToken = default)
        => await InvokeReconcileAsync(_service, cancellationToken);

    private static async Task InvokeReconcileAsync(
        IntegrityReconciliationService service,
        CancellationToken cancellationToken = default)
    {
        var method = typeof(IntegrityReconciliationService)
            .GetMethod("ReconcileAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var task = (Task)method.Invoke(service, [cancellationToken])!;
        await task;
    }
}
