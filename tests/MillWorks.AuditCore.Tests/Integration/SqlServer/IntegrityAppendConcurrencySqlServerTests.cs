using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

/// <summary>
/// Regression coverage for the <c>AuditIntegrity.SequenceNumber</c> duplicate-key race.
/// N concurrent writers — each on its own <see cref="AuditApplicationDbContext"/> and its
/// own <see cref="TamperDetectionService"/> instance — append one integrity record each.
/// The test asserts every write lands, sequences are dense 1..N, the hash chain is
/// continuous, and no retryable write-conflict log fires.
/// <para>
/// On SQL Server the service takes <c>sp_getapplock</c> inside the write transaction
/// and does <i>not</i> hold its process-local fallback semaphore (see
/// <see cref="IAuditIntegrityRepository.SupportsCrossProcessAppendLock"/>). That makes
/// this test a real canary for the applock: if a future change removes or weakens
/// <c>AcquireAppendLockAsync</c>, N writers on independent connections collide on
/// <c>IX_AuditIntegrity_SequenceNumber</c> and the <c>retryLogCount == 0</c> assertion
/// fires. If we held the semaphore on SQL Server too, this single-process test would
/// spuriously pass even with a broken applock — that's why the semaphore is gated on
/// <c>!SupportsCrossProcessAppendLock</c>.
/// </para>
/// </summary>
public sealed class IntegrityAppendConcurrencySqlServerTests : SqlServerTestBase
{
    private const int Writers = 32;
    private const string HmacKey = "sql-server-integrity-append-race-test-hmac-key";

    [Test]
    public async Task CreateIntegrityRecordAsync_Under32ParallelWriters_NoDuplicateKeyRaceAndChainIsContinuous()
    {
        // Single captured timestamp so writer-side hash input matches the verifier-side
        // re-hash after round-tripping through datetimeoffset (sub-tick drift would break
        // the chain-continuity assertion at the end of the test).
        var insertedDate = DateTimeOffset.UtcNow;

        // Seed N AuditEventEntity rows; each parallel writer will append an integrity
        // record for one of them.
        var dtos = new List<AuditIntegrityDto>(Writers);
        await using (var seedContext = CreateContext())
        {
            var entities = new List<AuditEventEntity>(Writers);
            for (int i = 0; i < Writers; i++)
            {
                entities.Add(new AuditEventEntity
                {
                    EventType = $"Append.Race.Event.{i}",
                    JsonData = $"{{\"index\":{i}}}",
                    User = "append-race-test",
                    UserId = Guid.NewGuid(),
                    InsertedDate = insertedDate
                });
            }

            seedContext.AuditEvents.AddRange(entities);
            await seedContext.SaveChangesAsync();

            foreach (var entity in entities)
            {
                dtos.Add(new AuditIntegrityDto
                {
                    EventId = entity.EventId,
                    EventType = entity.EventType,
                    User = entity.User,
                    UserId = entity.UserId,
                    JsonData = entity.JsonData,
                    InsertedDate = entity.InsertedDate
                });
            }
        }

        // Shared retry-log counter — every writer's TamperDetectionService logs into it.
        // "Retryable write conflict" fires only on DbUpdateException{ IsDuplicateKey || IsDeadlock }
        // inside the append critical section.
        var retryLogCount = 0;
        var logger = new CountingLogger(message =>
        {
            if (message.Contains("Retryable write conflict", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref retryLogCount);
            }
        });

        // One task per writer, each with its own DbContext / repositories / service.
        // This mirrors the multi-instance production topology where each API instance
        // holds its own pool of DbContexts against a shared SQL Server.
        await Parallel.ForEachAsync(
            dtos,
            new ParallelOptions { MaxDegreeOfParallelism = Writers },
            async (dto, ct) =>
            {
                await using var context = CreateContext();
                var service = CreateService(context, logger);
                await service.CreateIntegrityRecordAsync(dto, ct);
            });

        // Read the final chain back and assert structural invariants.
        List<AuditIntegrityEntity> chain;
        await using (var verifyContext = CreateContext())
        {
            chain = await verifyContext.AuditIntegrity
                .AsNoTracking()
                .OrderBy(static i => i.SequenceNumber)
                .ToListAsync();
        }

        Assert.Multiple(() =>
        {
            Assert.That(chain, Has.Count.EqualTo(Writers),
                "Every parallel writer must have persisted exactly one integrity row.");

            for (int k = 0; k < chain.Count; k++)
            {
                Assert.That(chain[k].SequenceNumber, Is.EqualTo(k + 1),
                    $"Sequence must be dense 1..N — observed gap or duplicate at index {k}.");
            }

            for (int k = 1; k < chain.Count; k++)
            {
                Assert.That(chain[k].PreviousEventHash, Is.EqualTo(chain[k - 1].EventHash),
                    $"Chain break: row {k + 1}.PreviousEventHash does not match row {k}.EventHash.");
            }

            Assert.That(chain[0].PreviousEventHash, Is.Null,
                "Genesis row's PreviousEventHash must be null.");

            Assert.That(retryLogCount, Is.Zero,
                "Cross-process serializer (sp_getapplock) should hold under contention — " +
                "any retry log indicates the applock was not effective and the chain fell back " +
                "to the DB-level unique-index race.");
        });
    }

    private static TamperDetectionService CreateService(
        AuditApplicationDbContext context,
        ILogger<TamperDetectionService> logger)
    {
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var securityEventService = new Mock<IAuditSecurityEventService>().Object;

        return new TamperDetectionService(
            eventRepo,
            integrityRepo,
            securityEventService,
            logger,
            Options.Create(new AuditOptions
            {
                Environment = "Development",
                HmacKey = HmacKey
            }),
            Options.Create(new SecurityOptions()));
    }

    /// <summary>
    /// Fan-in logger that forwards every formatted message to a counter delegate.
    /// Shared across the N writer services so we can assert zero "Retryable write
    /// conflict" logs after the Parallel.ForEachAsync returns.
    /// </summary>
    private sealed class CountingLogger(Action<string> onMessage) : ILogger<TamperDetectionService>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            onMessage(formatter(state, exception));
        }
    }
}
