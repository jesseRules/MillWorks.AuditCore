using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration.Garnet;

/// <summary>
/// Real-Garnet test for <see cref="RedisAuditDeadLetterQueue"/> covering the part the
/// in-memory/SQLite reprocess test cannot: the event survives a JSON serialize → Garnet →
/// deserialize round-trip with its <see cref="AuditEvent.EventId"/> intact, and reprocessing
/// the same logical event twice still yields a single audit row. The audit store is SQLite
/// (the reprocess writes flow through a scoped <c>AuditLogger</c>); Garnet is the dead-letter
/// transport under test.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class RedisDeadLetterReprocessGarnetTests : GarnetTestBase
{
    [Test]
    public async Task ReprocessSameEventTwice_ViaGarnet_PreservesEventIdAndYieldsSingleAuditRow()
    {
        // SQLite audit store, shared across every scoped context for the test's lifetime.
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<AuditDbContext>().UseSqlite(connection).Options;
        using (var schema = new AuditDbContext(options))
        {
            await schema.Database.EnsureCreatedAsync();
        }

        await using var provider =
            ReprocessIdempotencyTestSupport.BuildScopedAuditLoggerProvider(() => new AuditDbContext(options));

        using var dlq = new RedisAuditDeadLetterQueue(
            Multiplexer,
            new PassThroughAuditFieldRedactor(),
            NullLogger<RedisAuditDeadLetterQueue>.Instance,
            queueName: "audit:dlq:test:" + Guid.NewGuid().ToString("N"),
            serviceScopeFactory: provider.GetRequiredService<IServiceScopeFactory>());

        var eventId = Guid.NewGuid();
        var auditEvent = new AuditEvent
        {
            EventId = eventId,
            EventType = "Test.Reprocess",
            StartDate = DateTimeOffset.UtcNow
        };

        // The same logical event lands in the DLQ twice (e.g. two delivery attempts both failed),
        // so reprocessing both models the overlap a lapsed lock would allow.
        await dlq.StoreFailedEventAsync(auditEvent, new InvalidOperationException("failure 1"), "attempt 1");
        await dlq.StoreFailedEventAsync(auditEvent, new InvalidOperationException("failure 2"), "attempt 2");

        var entries = await dlq.GetFailedEventsAsync();
        entries.Should().HaveCount(2);
        entries.Should().OnlyContain(
            e => e.OriginalEvent != null && e.OriginalEvent.EventId == eventId,
            "EventId must survive the serialize -> Garnet -> deserialize round-trip");

        foreach (var entry in entries)
        {
            var reprocessed = await dlq.ReprocessEventAsync(entry.Id);
            reprocessed.Should().BeTrue(
                "each reprocess succeeds; the duplicate insert collides on the EventId primary key and is treated as success");
        }

        await using var verify = new AuditDbContext(options);
        var rowCount = await verify.Set<AuditEventEntity>().CountAsync(e => e.EventId == eventId);
        rowCount.Should().Be(1, "reprocessing the same logical event twice must dedupe to a single audit row");
    }
}
