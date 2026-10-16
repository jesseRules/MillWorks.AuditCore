using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Tests.Integration;

namespace MillWorks.AuditCore.Tests.Repositories;

[TestFixture]
public sealed class AuditSequenceIntegritySqliteTests : SqliteIntegrationFixture
{
    [TestCase(0, false, true)]
    [TestCase(1, false, true)]
    [TestCase(2100, false, true)]
    [TestCase(2100, true, false)]
    public async Task IsSequenceContiguousAsync_UsesOneAggregateCommand(int count, bool removeMiddle, bool expected)
    {
        await using (var seedContext = CreateContext())
        {
            for (var sequence = 1; sequence <= count; sequence++)
            {
                if (removeMiddle && sequence == count / 2) continue;
                var eventId = Guid.NewGuid();
                seedContext.AuditEvents.Add(new AuditEventEntity
                {
                    EventId = eventId,
                    EventType = "Test",
                    InsertedDate = DateTimeOffset.UtcNow
                });
                seedContext.AuditIntegrity.Add(new AuditIntegrityEntity
                {
                    EventId = eventId,
                    SequenceNumber = sequence,
                    EventHash = "hash",
                    Checksum = "checksum",
                    TrustedTimestamp = DateTimeOffset.UtcNow,
                    AlgorithmVersion = 1
                });
            }
            await seedContext.SaveChangesAsync();
        }

        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlite(Connection)
            .LogTo(commands.Add, new[] { RelationalEventId.CommandExecuted })
            .Options;
        await using var context = new AuditDbContext(options);
        var repository = new AuditIntegrityRepository(context);

        Assert.That(await repository.IsSequenceContiguousAsync(), Is.EqualTo(expected));
        Assert.That(commands, Has.Count.EqualTo(1));
        Assert.That(commands[0], Does.Contain("COUNT(*)").And.Contain("DISTINCT")
            .And.Contain("MIN(").And.Contain("MAX("));
        Assert.That(context.ChangeTracker.Entries(), Is.Empty);
    }
}
