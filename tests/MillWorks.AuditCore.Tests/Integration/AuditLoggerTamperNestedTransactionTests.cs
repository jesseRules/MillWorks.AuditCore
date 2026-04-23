using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Regression for the nested-transaction bug that appeared after moving hash-chain
/// serialization from <c>IAuditDistributedLockService</c> to a transaction-scoped
/// <c>sp_getapplock</c>. <c>AuditLogger.LogAsync</c> in strict mode opens a transaction
/// via <c>auditEventRepository.ExecuteInTransactionAsync</c> and, inside that lambda,
/// calls <c>tamperDetectionService.CreateIntegrityRecordAsync</c>. Both repositories
/// share the same <see cref="AuditApplicationDbContext"/>. If <c>CreateIntegrityRecordAsync</c>
/// tries to open its own nested <c>ExecuteInTransactionAsync</c>, EF throws
/// "connection already in a transaction". The rollback leaves the already-added
/// <c>AuditEventEntity</c> in the change tracker, so <c>ResilientAuditLogger</c>'s
/// retry hits an identity-map conflict on the same EventId.
/// <para>
/// This test exercises the exact path. The fix: <c>TamperDetectionService</c> detects
/// an active outer transaction on the shared context and joins it instead of nesting.
/// </para>
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class AuditLoggerTamperNestedTransactionTests : SqliteIntegrationFixture
{
    [Test]
    public async Task LogAsync_WithTamperDetection_JoinsOuterTransaction_NoEntityTrackingConflict()
    {
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var securityEventService = new Mock<IAuditSecurityEventService>().Object;

        var tamperDetectionService = new TamperDetectionService(
            eventRepo,
            integrityRepo,
            securityEventService,
            NullLogger<TamperDetectionService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AuditOptions
            {
                Environment = "Development",
                HmacKey = "nested-txn-regression-test-hmac-key-32"
            }),
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions { EnableTamperDetection = true }));

        var auditLogger = new AuditLogger(
            NullLogger<AuditLogger>.Instance,
            Mock.Of<IAuditEventFactory>(),
            eventRepo,
            context,
            Mock.Of<IAuditContext>(),
            new PassThroughAuditFieldRedactor(),
            tamperDetectionService: tamperDetectionService,
            integrityWriteBatcher: null,
            securityOptions: Microsoft.Extensions.Options.Options.Create(new SecurityOptions
            {
                EnableTamperDetection = true,
                EnableBatchedIntegrityWrites = false
            }));

        // Two consecutive LogAsync calls on the same DI scope / same DbContext.
        // The second call fails on the pre-fix code because the first call's nested
        // ExecuteInTransactionAsync aborts mid-txn and strands the AuditEventEntity
        // in the change tracker — AddAsync on the second event with a new instance
        // but same EventId-space hits the identity-map conflict.
        //
        // (The actual repro hit the conflict on *retry* of one failing LogAsync, but
        // the root cause — stranded tracked entity after a nested-txn throw — is
        // directly observable by either repeating or retrying within one scope.
        // Repeating is the cleaner assertion.)
        await auditLogger.LogAsync(NewEvent("First"));
        await auditLogger.LogAsync(NewEvent("Second"));

        using var verifyContext = CreateContext();

        var events = await verifyContext.AuditEvents
            .AsNoTracking()
            .OrderBy(e => e.InsertedDate)
            .ToListAsync();

        var integrity = await verifyContext.AuditIntegrity
            .AsNoTracking()
            .OrderBy(i => i.SequenceNumber)
            .ToListAsync();

        Assert.Multiple(() =>
        {
            Assert.That(events, Has.Count.EqualTo(2),
                "Both strict-mode audit writes should commit their AuditEventEntity rows.");
            Assert.That(events.All(e => e.IntegrityStatus == IntegrityStatus.Completed), Is.True,
                "Strict-mode writes commit event and integrity atomically — every event must be Completed.");

            Assert.That(integrity, Has.Count.EqualTo(2),
                "Every strict-mode write must persist exactly one integrity row.");
            Assert.That(integrity[0].SequenceNumber, Is.EqualTo(1));
            Assert.That(integrity[1].SequenceNumber, Is.EqualTo(2));
            Assert.That(integrity[0].PreviousEventHash, Is.Null,
                "Genesis row's PreviousEventHash is null.");
            Assert.That(integrity[1].PreviousEventHash, Is.EqualTo(integrity[0].EventHash),
                "Chain continuity must hold across the two strict-mode writes.");
        });
    }

    private static AuditEvent NewEvent(string tag) => new()
    {
        EventId = Guid.NewGuid(),
        EventType = $"Test.NestedTxn.{tag}",
        StartDate = DateTimeOffset.UtcNow,
        EndDate = DateTimeOffset.UtcNow
    };
}
