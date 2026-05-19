using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Regression for the change-tracker coupling between <c>TamperDetectionService</c>'s
/// retry path and outer-transaction participants sharing the same <c>DbContext</c>.
/// <para>
/// Before the fix, the retry catch block called <c>ClearChangeTrackerAsync</c>, which
/// detached every tracked entity — including <c>AuditEventEntity</c> rows that an
/// outer transaction had added but not yet flushed. A later <c>SaveChanges</c> then
/// became a no-op and the event was silently dropped on commit. The fix detaches only
/// the integrity entity the failed attempt added.
/// </para>
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class TamperDetectionRetryChangeTrackerTests : SqliteIntegrationFixture
{
    [Test]
    public async Task CreateIntegrityRecordAsync_DuplicateKeyRetry_LeavesOuterTrackedEntitiesIntact()
    {
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var realIntegrityRepo = new AuditIntegrityRepository(context);

        // Proxy IAuditIntegrityRepository so most calls pass through to the real repo
        // (writes land in EF's change tracker on the shared context) but SaveChangesAsync
        // throws a SQLite-shaped duplicate-key exception on its first call. This is the
        // exact retry path the service takes under an outer transaction.
        var saveCount = 0;
        var proxy = new Mock<IAuditIntegrityRepository>();

        // Non-SQL-Server provider — exercise the local-semaphore fallback, matching the
        // real SQLite test fixture. Not load-bearing for this regression, but consistent.
        proxy.SetupGet(x => x.SupportsCrossProcessAppendLock).Returns(false);
        // Surface the outer transaction so the service joins it instead of opening a nested
        // one — the whole point of this regression is what happens to OTHER entities in
        // that outer transaction when the integrity save retries.
        proxy.SetupGet(x => x.CurrentTransaction).Returns(() => context.Database.CurrentTransaction);
        proxy.Setup(x => x.AcquireAppendLockAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        proxy.Setup(x => x.GetLatestBySequenceAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(ct => realIntegrityRepo.GetLatestBySequenceAsync(ct));
        proxy.Setup(x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Returns<AuditIntegrityEntity, CancellationToken>((e, ct) => realIntegrityRepo.AddAsync(e, ct));
        proxy.Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(async ct =>
            {
                var n = Interlocked.Increment(ref saveCount);
                if (n == 1)
                {
                    throw new DbUpdateException(
                        "Simulated unique-constraint violation",
                        new Exception("UNIQUE constraint failed: AuditIntegrity.SequenceNumber"));
                }
                return await realIntegrityRepo.SaveChangesAsync(ct);
            });
        proxy.Setup(x => x.DetachAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Returns<AuditIntegrityEntity, CancellationToken>((e, ct) => realIntegrityRepo.DetachAsync(e, ct));

        var tamperService = new TamperDetectionService(
            eventRepo,
            proxy.Object,
            Mock.Of<IAuditSecurityEventService>(),
            NullLogger<TamperDetectionService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AuditOptions
            {
                Environment = "Development",
                HmacKey = "retry-change-tracker-regression-hmac-32"
            }),
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions()));

        var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        // Defer the event flush until after the integrity call. Pre-fix, the retry's
        // ClearChangeTrackerAsync detaches the still-Added AuditEventEntity, the final
        // SaveChanges no-ops, and the row is never persisted. Post-fix, only the failed
        // integrity entity is detached and the final SaveChanges flushes the event.
        await eventRepo.ExecuteInTransactionAsync(async () =>
        {
            await eventRepo.AddAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Test.RetryChangeTracker",
                InsertedDate = now,
                LastUpdatedDate = now
            });

            await tamperService.CreateIntegrityRecordAsync(new AuditIntegrityDto
            {
                EventId = eventId,
                InsertedDate = now,
                EventType = "Test.RetryChangeTracker"
            });

            await eventRepo.SaveChangesAsync();
        });

        using var verifyContext = CreateContext();
        var eventRow = await verifyContext.AuditEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId);
        var integrityRow = await verifyContext.AuditIntegrity
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.EventId == eventId);

        Assert.Multiple(() =>
        {
            Assert.That(saveCount, Is.EqualTo(2),
                "Exactly one retry expected: first save throws the duplicate-key stub, second succeeds.");
            Assert.That(eventRow, Is.Not.Null,
                "Pre-fix: the integrity retry's ClearChangeTrackerAsync detaches the outer " +
                "transaction's pending AuditEventEntity, the deferred SaveChanges no-ops, and the " +
                "event row is lost. Post-fix: only the failed integrity entity is detached.");
            Assert.That(integrityRow, Is.Not.Null,
                "The integrity row committed by the successful retry must also be persisted.");
            proxy.Verify(x => x.DetachAsync(
                It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()),
                Times.Once,
                "The retry cleanup must detach exactly the failed integrity entity, not the whole tracker.");
        });
    }
}
