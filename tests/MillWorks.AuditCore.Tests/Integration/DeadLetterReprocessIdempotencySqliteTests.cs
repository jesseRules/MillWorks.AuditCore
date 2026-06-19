using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Regression tests for RedisJobQueueDurability finding #4: the distributed lock guarding
/// dead-letter reprocessing is an efficiency optimization, not a correctness dependency.
/// Correctness is guaranteed at the resource layer — re-emitting the same dead-letter event
/// is idempotent because <see cref="AuditEvent.EventId"/> is stable and is the primary key
/// of <see cref="AuditEventEntity"/>, so a duplicate insert collides and is swallowed as
/// success. These tests lock that guarantee in place against SQLite; sibling fixtures cover
/// real Garnet (JSON round-trip) and SQL Server (SqlException duplicate-key branch).
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class DeadLetterReprocessIdempotencySqliteTests : SqliteIntegrationFixture
{
    [Test]
    public async Task ReprocessEventTwice_SameDeadLetterEvent_YieldsSingleAuditRow()
    {
        // Simulates the finding #4 race: the reprocess lock lapses and two processors
        // reprocess the same dead-letter entry before either marks it processed.
        await using var provider =
            ReprocessIdempotencyTestSupport.BuildScopedAuditLoggerProvider(() => new AuditDbContext(Options));

        var dlq = new InMemoryAuditDeadLetterQueue(
            NullLogger<InMemoryAuditDeadLetterQueue>.Instance,
            provider,
            new PassThroughAuditFieldRedactor());

        var eventId = Guid.NewGuid();
        await dlq.StoreFailedEventAsync(
            new AuditEvent
            {
                EventId = eventId,
                EventType = "Test.Reprocess",
                StartDate = DateTimeOffset.UtcNow
            },
            new InvalidOperationException("original failure"),
            "test failure");

        var deadLetterId = (await dlq.GetFailedEventsAsync()).Single().Id;

        // Two reprocesses of the same event — modelling the overlap a lapsed lock allows.
        var first = await dlq.ReprocessEventAsync(deadLetterId);
        var second = await dlq.ReprocessEventAsync(deadLetterId);

        first.Should().BeTrue("the first reprocess writes the audit row");
        second.Should().BeTrue(
            "the duplicate insert collides on the EventId primary key and is treated as success, not an error");

        await using var verify = CreateContext();
        var rowCount = await verify.Set<AuditEventEntity>().CountAsync(e => e.EventId == eventId);
        rowCount.Should().Be(1, "the overlapping reprocess must not create a duplicate audit row");
    }

    [Test]
    public async Task LogAsync_SameEventFromSeparateContexts_PersistsSingleRowAndDoesNotThrow()
    {
        // The load-bearing mechanism the reprocess path relies on, in isolation. Production
        // runs each LogAsync on its own scoped context (the DLQ creates a fresh scope per
        // reprocess), so a second write of the same EventId reaches the database and collides
        // on the EventId primary key as a DbUpdateException, which AuditLogger swallows as
        // success — no duplicate row, no throw.
        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Idempotent",
            StartDate = DateTimeOffset.UtcNow
        };

        await using (var firstContext = CreateContext())
        {
            await ReprocessIdempotencyTestSupport.CreateAuditLogger(firstContext).LogAsync(auditEvent);
        }

        await using (var secondContext = CreateContext())
        {
            var secondLog = async () =>
                await ReprocessIdempotencyTestSupport.CreateAuditLogger(secondContext).LogAsync(auditEvent);
            await secondLog.Should().NotThrowAsync();
        }

        await using var verify = CreateContext();
        var rowCount = await verify.Set<AuditEventEntity>().CountAsync(e => e.EventId == auditEvent.EventId);
        rowCount.Should().Be(1, "re-logging the same EventId from a fresh context must dedupe to a single row");
    }
}
