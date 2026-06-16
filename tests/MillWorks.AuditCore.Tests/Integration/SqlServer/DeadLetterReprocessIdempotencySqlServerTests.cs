using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

/// <summary>
/// SQL Server counterpart to the dead-letter reprocess idempotency tests. The SQLite test hits
/// the <c>"UNIQUE constraint"</c> branch of <c>DuplicateKeyDetector</c>; this one drives the
/// real <c>SqlException</c> 2627/2601 branch, confirming that reprocessing the same event twice
/// against SQL Server still yields a single audit row (RedisJobQueueDurability finding #4).
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class DeadLetterReprocessIdempotencySqlServerTests : SqlServerTestBase
{
    [Test]
    public async Task ReprocessSameEventTwice_OnSqlServer_YieldsSingleAuditRow()
    {
        // Each reprocess resolves a fresh AuditLogger over a new context against the same DB,
        // matching production wiring.
        await using var provider =
            ReprocessIdempotencyTestSupport.BuildScopedAuditLoggerProvider(SqlServerContainerFixture.CreateContext);

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

        // The in-memory DLQ does not remove on success, so reprocessing the same id twice models
        // two processors overlapping after a lapsed lock.
        var first = await dlq.ReprocessEventAsync(deadLetterId);
        var second = await dlq.ReprocessEventAsync(deadLetterId);

        first.Should().BeTrue("the first reprocess writes the audit row");
        second.Should().BeTrue(
            "the duplicate insert hits the EventId primary key as SqlException 2627/2601, which is treated as success");

        await using var verify = CreateContext();
        var rowCount = await verify.Set<AuditEventEntity>().CountAsync(e => e.EventId == eventId);
        rowCount.Should().Be(1, "the overlapping reprocess must not create a duplicate audit row on SQL Server");
    }
}
