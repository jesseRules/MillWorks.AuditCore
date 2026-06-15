using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Regression tests for Finding #1 (CodeReview2026-06-09): batch duplicate-key handling.
/// When a batch contains both duplicate and new events, the new events must be persisted —
/// the previous behavior marked the entire batch as duplicate on any duplicate key error,
/// silently losing new events.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class AuditLoggerBatchDuplicateSqliteTests : SqliteIntegrationFixture
{
    [Test]
    public async Task LogBatchAsync_MixedBatchWithOneDuplicate_PersistsNewEvent()
    {
        // Arrange: insert event A directly so it already exists
        var eventAId = Guid.NewGuid();
        var eventBId = Guid.NewGuid();

        using (var setupContext = CreateContext())
        {
            setupContext.Set<AuditEventEntity>().Add(new AuditEventEntity
            {
                EventId = eventAId,
                EventType = "Test.EventA",
                InsertedDate = DateTimeOffset.UtcNow,
                LastUpdatedDate = DateTimeOffset.UtcNow,
                JsonData = "{}"
            });
            await setupContext.SaveChangesAsync();
        }

        // Verify event A exists
        using (var verifyContext = CreateContext())
        {
            var exists = await verifyContext.Set<AuditEventEntity>().AnyAsync(e => e.EventId == eventAId);
            Assert.That(exists, Is.True, "Event A should exist before batch");
        }

        // Act: log a batch containing event A (duplicate) and event B (new)
        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var securityOptions = Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EnableTamperDetection = false,
            EnableBatchedIntegrityWrites = false
        });

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            repository,
            context,
            Mock.Of<IAuditContext>(),
            new PassThroughAuditFieldRedactor(),
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: securityOptions);

        var eventA = new AuditEvent
        {
            EventId = eventAId,
            EventType = "Test.EventA",
            StartDate = DateTimeOffset.UtcNow
        };

        var eventB = new AuditEvent
        {
            EventId = eventBId,
            EventType = "Test.EventB",
            StartDate = DateTimeOffset.UtcNow
        };

        var result = await auditLogger.LogBatchAsync([eventA, eventB]);

        // Assert: batch should succeed and event B must exist
        Assert.That(result.Success, Is.True, "Batch should report success");

        using var verifyContext2 = CreateContext();
        var eventBExists = await verifyContext2.Set<AuditEventEntity>().AnyAsync(e => e.EventId == eventBId);
        Assert.That(eventBExists, Is.True, "Event B (new) must be persisted, not lost");

        var eventAStillExists = await verifyContext2.Set<AuditEventEntity>().AnyAsync(e => e.EventId == eventAId);
        Assert.That(eventAStillExists, Is.True, "Event A should still exist");
    }

    [Test]
    public async Task LogBatchAsync_AllDuplicates_ReportsDuplicate()
    {
        // Arrange: insert both events directly
        var eventAId = Guid.NewGuid();
        var eventBId = Guid.NewGuid();

        using (var setupContext = CreateContext())
        {
            setupContext.Set<AuditEventEntity>().Add(new AuditEventEntity
            {
                EventId = eventAId,
                EventType = "Test.EventA",
                InsertedDate = DateTimeOffset.UtcNow,
                LastUpdatedDate = DateTimeOffset.UtcNow,
                JsonData = "{}"
            });
            setupContext.Set<AuditEventEntity>().Add(new AuditEventEntity
            {
                EventId = eventBId,
                EventType = "Test.EventB",
                InsertedDate = DateTimeOffset.UtcNow,
                LastUpdatedDate = DateTimeOffset.UtcNow,
                JsonData = "{}"
            });
            await setupContext.SaveChangesAsync();
        }

        // Act: log a batch where both events are duplicates
        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var securityOptions = Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EnableTamperDetection = false,
            EnableBatchedIntegrityWrites = false
        });

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            repository,
            context,
            Mock.Of<IAuditContext>(),
            new PassThroughAuditFieldRedactor(),
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: securityOptions);

        var eventA = new AuditEvent
        {
            EventId = eventAId,
            EventType = "Test.EventA",
            StartDate = DateTimeOffset.UtcNow
        };

        var eventB = new AuditEvent
        {
            EventId = eventBId,
            EventType = "Test.EventB",
            StartDate = DateTimeOffset.UtcNow
        };

        var result = await auditLogger.LogBatchAsync([eventA, eventB]);

        // Assert: batch should report as duplicate when ALL events are duplicates
        Assert.That(result.Success, Is.True, "Batch should report success");
        Assert.That(result.IsDuplicate, Is.True, "Batch should be marked as duplicate when all events are duplicates");
    }

    [Test]
    public async Task LogBatchAsync_AllNew_ReportsSuccess()
    {
        // Arrange: no pre-existing events
        var eventAId = Guid.NewGuid();
        var eventBId = Guid.NewGuid();

        using var context = CreateContext();
        var repository = new AuditEventRepository(context);
        var securityOptions = Microsoft.Extensions.Options.Options.Create(new SecurityOptions
        {
            EnableTamperDetection = false,
            EnableBatchedIntegrityWrites = false
        });

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            repository,
            context,
            Mock.Of<IAuditContext>(),
            new PassThroughAuditFieldRedactor(),
            tamperDetectionService: null,
            integrityWriteBatcher: null,
            securityOptions: securityOptions);

        var eventA = new AuditEvent
        {
            EventId = eventAId,
            EventType = "Test.EventA",
            StartDate = DateTimeOffset.UtcNow
        };

        var eventB = new AuditEvent
        {
            EventId = eventBId,
            EventType = "Test.EventB",
            StartDate = DateTimeOffset.UtcNow
        };

        var result = await auditLogger.LogBatchAsync([eventA, eventB]);

        // Assert: batch should report success (not duplicate)
        Assert.That(result.Success, Is.True, "Batch should report success");
        Assert.That(result.IsDuplicate, Is.False, "Batch should NOT be marked as duplicate for new events");

        using var verifyContext = CreateContext();
        var count = await verifyContext.Set<AuditEventEntity>().CountAsync();
        Assert.That(count, Is.EqualTo(2), "Both new events should be persisted");
    }
}
