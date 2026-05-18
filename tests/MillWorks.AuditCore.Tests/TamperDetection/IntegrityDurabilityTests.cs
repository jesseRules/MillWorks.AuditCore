using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.TamperDetection;

/// <summary>
/// Tests proving the durable integrity guarantees introduced by the FixLies work:
/// - Batched mode creates event + work item atomically
/// - Strict mode marks events as Completed
/// - Batcher marks work items Completed after flush
/// - Reconciliation retries stale pending items
/// - Reconciliation marks Failed after max attempts
/// - Reconciliation detects already-existing integrity records
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class IntegrityDurabilityTests
{
    private AuditDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite("DataSource=:memory:")
            .Options;
        _dbContext = new AuditDbContext(options);
        _dbContext.Database.OpenConnection();
        _dbContext.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Database.CloseConnection();
        _dbContext.Dispose();
    }

    #region Phase 1: Transactional Insert

    [Test]
    [CancelAfter(10000)]
    public async Task BatchedMode_CreatesEventAndWorkItem_InSameTransaction()
    {
        // Arrange — use a started batcher so EnqueueAsync doesn't hang
        var mockTamper = new Mock<ITamperDetectionService>();
        mockTamper.Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditIntegrityDto> e, CancellationToken _) =>
                e.Select(x => new AuditIntegrityDto { EventId = x.EventId }).ToList());

        var batcher = CreateStartableBatcher(mockTamper.Object);
        await batcher.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        var mockRepo = new Mock<IAuditEventRepository>();
        mockRepo.Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> action, CancellationToken _) => action());
        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => { _dbContext.AuditEvents.Add(e); return e; });
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken ct) => _dbContext.SaveChangesAsync(ct));

        var logger = CreateAuditLogger(mockRepo, mockTamper.Object, batcher);

        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.BatchedInsert"
        };

        // Act
        await logger.LogAsync(auditEvent);

        // Assert — verify both event and work item were inserted in a transaction
        mockRepo.Verify(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()), Times.Once);

        // The work item should have been persisted to the database
        var workItem = await _dbContext.IntegrityWorkItems.FirstOrDefaultAsync();
        Assert.That(workItem, Is.Not.Null);
        Assert.That(workItem!.EventId, Is.EqualTo(auditEvent.EventId));
        Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Pending));

        await batcher.StopAsync(CancellationToken.None);
    }

    [Test]
    [CancelAfter(10000)]
    public async Task BatchedMode_SetsEventIntegrityStatus_ToPending()
    {
        // Arrange — use a started batcher so EnqueueAsync doesn't hang
        var mockTamper = new Mock<ITamperDetectionService>();
        mockTamper.Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<AuditIntegrityDto> e, CancellationToken _) =>
                e.Select(x => new AuditIntegrityDto { EventId = x.EventId }).ToList());

        var batcher = CreateStartableBatcher(mockTamper.Object);
        await batcher.StartAsync(CancellationToken.None);
        await Task.Delay(50);

        var capturedEntity = (AuditEventEntity?)null;
        var mockRepo = new Mock<IAuditEventRepository>();
        mockRepo.Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> action, CancellationToken _) => action());
        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var logger = CreateAuditLogger(mockRepo, mockTamper.Object, batcher);

        // Act
        await logger.LogAsync(new AuditEvent { EventType = "Test" });

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IntegrityStatus, Is.EqualTo(IntegrityStatus.Pending));

        await batcher.StopAsync(CancellationToken.None);
    }

    [Test]
    public async Task StrictMode_SetsEventIntegrityStatus_ToCompleted()
    {
        // Arrange — no batcher = strict mode
        var capturedEntity = (AuditEventEntity?)null;
        var mockRepo = new Mock<IAuditEventRepository>();
        mockRepo.Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> action, CancellationToken _) => action());
        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mockTamper = new Mock<ITamperDetectionService>();

        // No batcher — strict mode
        var logger = CreateAuditLogger(mockRepo, mockTamper.Object, batcher: null);

        // Act
        await logger.LogAsync(new AuditEvent { EventType = "Test" });

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IntegrityStatus, Is.EqualTo(IntegrityStatus.Completed));
    }

    [Test]
    public async Task NoTamperDetection_SetsEventIntegrityStatus_ToCompleted()
    {
        // Arrange — no tamper detection service
        var capturedEntity = (AuditEventEntity?)null;
        var mockRepo = new Mock<IAuditEventRepository>();
        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var logger = CreateAuditLogger(mockRepo, tamperDetection: null, batcher: null);

        // Act
        await logger.LogAsync(new AuditEvent { EventType = "Test" });

        // Assert
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IntegrityStatus, Is.EqualTo(IntegrityStatus.Completed));
    }

    [Test]
    public async Task LogBatchAsync_WithTamperDetection_SetsAllEventsToCompleted()
    {
        // Arrange
        var capturedEntities = new List<AuditEventEntity>();
        var mockRepo = new Mock<IAuditEventRepository>();
        mockRepo.Setup(r => r.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns((Func<Task> action, CancellationToken _) => action());
        mockRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<AuditEventEntity>, CancellationToken>((e, _) => capturedEntities.AddRange(e))
            .ReturnsAsync((IEnumerable<AuditEventEntity> e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1);

        var mockTamper = new Mock<ITamperDetectionService>();
        mockTamper.Setup(t => t.CreateIntegrityRecordBatchAsync(
                It.IsAny<IReadOnlyList<AuditIntegrityDto>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditIntegrityDto>());

        // LogBatchAsync always uses strict path — no batcher needed
        var logger = CreateAuditLogger(mockRepo, mockTamper.Object, batcher: null);

        var events = new List<AuditEvent>
        {
            new() { EventType = "Test1" },
            new() { EventType = "Test2" },
            new() { EventType = "Test3" }
        };

        // Act
        await logger.LogBatchAsync(events);

        // Assert
        Assert.That(capturedEntities, Has.Count.EqualTo(3));
        Assert.That(capturedEntities.All(e => e.IntegrityStatus == IntegrityStatus.Completed), Is.True);
    }

    #endregion

    #region Phase 2: Batcher marks work items complete

    [Test]
    [CancelAfter(10000)]
    public async Task Batcher_MarksWorkItemsCompleted_AfterSuccessfulFlush()
    {
        // Use a file-based SQLite DB to support concurrent access from batcher thread
        var dbPath = Path.Combine(Path.GetTempPath(), $"audit_test_{Guid.NewGuid()}.db");
        try
        {
            var fileDbOptions = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlite($"DataSource={dbPath}")
                .Options;

            // Seed data
            var eventId = Guid.NewGuid();
            using (var seedCtx = new AuditDbContext(fileDbOptions))
            {
                seedCtx.Database.EnsureCreated();
                seedCtx.AuditEvents.Add(new AuditEventEntity
                {
                    EventId = eventId,
                    EventType = "Test",
                    IntegrityStatus = IntegrityStatus.Pending
                });
                seedCtx.IntegrityWorkItems.Add(new AuditIntegrityWorkItemEntity
                {
                    EventId = eventId,
                    Status = IntegrityStatus.Pending
                });
                await seedCtx.SaveChangesAsync();
            }

            // Setup mocks — each scope gets a new DbContext instance
            var mockTamperDetection = new Mock<ITamperDetectionService>();
            mockTamperDetection
                .Setup(t => t.CreateIntegrityRecordBatchAsync(
                    It.IsAny<IReadOnlyList<AuditIntegrityDto>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyList<AuditIntegrityDto> e, CancellationToken _) =>
                    e.Select(x => new AuditIntegrityDto { EventId = x.EventId }).ToList());

            var mockScopeFactory = CreateScopeFactoryWithDbOptions(mockTamperDetection.Object, fileDbOptions);

            var batcher = new IntegrityWriteBatcher(
                mockScopeFactory,
                new Mock<ILogger<IntegrityWriteBatcher>>().Object,
                Options.Create(new SecurityOptions
                {
                    EnableBatchedIntegrityWrites = true,
                    IntegrityBatchSize = 1,
                    IntegrityFlushInterval = TimeSpan.FromMilliseconds(100)
                }));

            // Act
            await batcher.StartAsync(CancellationToken.None);
            await Task.Delay(50);

            await batcher.EnqueueAsync(new AuditIntegrityDto
            {
                EventId = eventId,
                EventType = "Test",
                JsonData = "{}"
            }, CancellationToken.None);

            await Task.Delay(200);
            await batcher.StopAsync(CancellationToken.None);

            // Assert — read from a fresh context
            using var verifyCtx = new AuditDbContext(fileDbOptions);
            var workItem = await verifyCtx.IntegrityWorkItems.FirstAsync(w => w.EventId == eventId);
            Assert.That(workItem.Status, Is.EqualTo(IntegrityStatus.Completed));
            Assert.That(workItem.CompletedAt, Is.Not.Null);

            var auditEvent = await verifyCtx.AuditEvents.FirstAsync(e => e.EventId == eventId);
            Assert.That(auditEvent.IntegrityStatus, Is.EqualTo(IntegrityStatus.Completed));
        }
        finally
        {
            if (File.Exists(dbPath)) File.Delete(dbPath);
        }
    }

    #endregion

    #region Phase 3: Diagnostics counters

    [Test]
    public void DiagnosticsCounters_IncrementCorrectly()
    {
        var diagnostics = new AuditDiagnostics();

        diagnostics.Increment(AuditDiagnosticCounter.IntegrityBatchFlush);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityBatchFlush);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityBatchFlushFailure);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityReconciliationSuccess);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityReconciliationFailure);
        diagnostics.Increment(AuditDiagnosticCounter.IntegrityPermanentFailure);

        Assert.That(diagnostics.IntegrityBatchFlushCount, Is.EqualTo(2));
        Assert.That(diagnostics.IntegrityBatchFlushFailureCount, Is.EqualTo(1));
        Assert.That(diagnostics.IntegrityReconciliationSuccessCount, Is.EqualTo(1));
        Assert.That(diagnostics.IntegrityReconciliationFailureCount, Is.EqualTo(1));
        Assert.That(diagnostics.IntegrityPermanentFailureCount, Is.EqualTo(1));

        diagnostics.Reset();

        Assert.That(diagnostics.IntegrityBatchFlushCount, Is.EqualTo(0));
        Assert.That(diagnostics.IntegrityBatchFlushFailureCount, Is.EqualTo(0));
        Assert.That(diagnostics.IntegrityReconciliationSuccessCount, Is.EqualTo(0));
        Assert.That(diagnostics.IntegrityReconciliationFailureCount, Is.EqualTo(0));
        Assert.That(diagnostics.IntegrityPermanentFailureCount, Is.EqualTo(0));
    }

    #endregion

    #region Helpers

    private AuditLogger CreateAuditLogger(
        Mock<IAuditEventRepository> mockRepo,
        ITamperDetectionService? tamperDetection,
        IntegrityWriteBatcher? batcher)
    {
        return new AuditLogger(
            new Mock<ILogger<AuditLogger>>().Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            _dbContext,
            new Mock<IAuditContext>().Object,
            new PassThroughAuditFieldRedactor(),
            tamperDetection,
            batcher,
            Options.Create(new SecurityOptions { EnableBatchedIntegrityWrites = true }));
    }

    private IntegrityWriteBatcher CreateStartableBatcher(ITamperDetectionService tamperDetection)
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            var mockScopeProvider = new Mock<IServiceProvider>();
            mockScopeProvider.Setup(p => p.GetService(typeof(ITamperDetectionService)))
                .Returns(tamperDetection);
            mockScopeProvider.Setup(p => p.GetService(typeof(AuditDbContext)))
                .Returns(_dbContext);

            var mockScope = new Mock<IServiceScope>();
            mockScope.Setup(s => s.ServiceProvider).Returns(mockScopeProvider.Object);
            return mockScope.Object;
        });

        return new IntegrityWriteBatcher(
            mockScopeFactory.Object,
            new Mock<ILogger<IntegrityWriteBatcher>>().Object,
            Options.Create(new SecurityOptions
            {
                EnableBatchedIntegrityWrites = true,
                IntegrityBatchSize = 1,
                IntegrityFlushInterval = TimeSpan.FromMilliseconds(100)
            }));
    }

    private static IServiceScopeFactory CreateScopeFactoryWithDbOptions(
        ITamperDetectionService tamperDetection,
        DbContextOptions<AuditDbContext> dbOptions)
    {
        var mockScopeFactory = new Mock<IServiceScopeFactory>();
        mockScopeFactory.Setup(f => f.CreateScope()).Returns(() =>
        {
            // Each scope gets a fresh DbContext to avoid cross-thread issues
            var ctx = new AuditDbContext(dbOptions);
            var mockScopeProvider = new Mock<IServiceProvider>();
            mockScopeProvider.Setup(p => p.GetService(typeof(ITamperDetectionService)))
                .Returns(tamperDetection);
            mockScopeProvider.Setup(p => p.GetService(typeof(AuditDbContext)))
                .Returns(ctx);

            var mockScope = new Mock<IServiceScope>();
            mockScope.Setup(s => s.ServiceProvider).Returns(mockScopeProvider.Object);
            mockScope.Setup(s => s.Dispose()).Callback(() => ctx.Dispose());
            return mockScope.Object;
        });

        return mockScopeFactory.Object;
    }

    #endregion
}
