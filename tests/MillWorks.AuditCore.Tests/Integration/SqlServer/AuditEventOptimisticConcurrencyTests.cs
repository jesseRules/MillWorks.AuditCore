using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class AuditEventOptimisticConcurrencyTests : SqlServerTestBase
{
    [Test]
    public async Task RowVersion_RoundTrip_PopulatesOnInsertAndChangesOnUpdate()
    {
        var insertedEvent = new AuditEventEntity
        {
            EventType = "RowVersionTest",
            JsonData = "{}"
        };
        var eventId = insertedEvent.EventId;

        await using (var ctx = CreateContext())
        {
            ctx.Set<AuditEventEntity>().Add(insertedEvent);
            await ctx.SaveChangesAsync();
        }

        byte[] firstRowVersion;
        await using (var ctx = CreateContext())
        {
            var reloaded = await ctx.Set<AuditEventEntity>()
                .AsNoTracking()
                .SingleAsync(e => e.EventId == eventId);
            firstRowVersion = reloaded.RowVersion;
        }

        await using (var ctx = CreateContext())
        {
            var trackedAfterInsert = await ctx.Set<AuditEventEntity>()
                .SingleAsync(e => e.EventId == eventId);
            trackedAfterInsert.IntegrityStatus = IntegrityStatus.Completed;
            await ctx.SaveChangesAsync();
        }

        byte[] secondRowVersion;
        IntegrityStatus reloadedStatus;
        await using (var ctx = CreateContext())
        {
            var reloaded = await ctx.Set<AuditEventEntity>()
                .AsNoTracking()
                .SingleAsync(e => e.EventId == eventId);
            secondRowVersion = reloaded.RowVersion;
            reloadedStatus = reloaded.IntegrityStatus;
        }

        Assert.Multiple(() =>
        {
            Assert.That(firstRowVersion, Is.Not.Null,
                "RowVersion should be populated by SQL Server on insert.");
            Assert.That(firstRowVersion, Is.Not.Empty,
                "RowVersion should be a non-empty byte array after insert.");
            Assert.That(reloadedStatus, Is.EqualTo(IntegrityStatus.Completed),
                "IntegrityStatus update should round-trip.");
            Assert.That(secondRowVersion, Is.Not.EqualTo(firstRowVersion),
                "SQL Server's rowversion column should change automatically when the row is updated.");
        });
    }
}
