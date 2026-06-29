using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class TamperChain10kSqlServerTests : SqlServerTestBase
{
    private const int TotalRows = 10_000;
    private const int BatchSize = 1_000;
    private const string HmacKey = "sql-server-tamper-chain-10k-test-hmac-key-32";
    private const string HmacKeyId = "sqlserver-tamper-chain-hmac-v1";

    [Test]
    public async Task VerifyChainIntegrityAsync_OverChainOf10000Rows_ReturnsValidResult()
    {
        // Single captured timestamp for all 10k events: eliminates any sub-tick precision
        // mismatch between the writer-side hash input (DTO.InsertedDate) and the verifier-side
        // re-hash (read back from datetimeoffset column).
        var insertedDate = DateTimeOffset.UtcNow;
        const string stableUser = "tamper-chain-test";
        var stableUserId = Guid.NewGuid();

        // Insert 10k AuditEvents in 10 chunks; each chunk uses its own short-lived context
        // so the change tracker never holds more than BatchSize entities at a time.
        var allEventDtos = new List<AuditIntegrityDto>(TotalRows);
        for (int chunkIndex = 0; chunkIndex < TotalRows / BatchSize; chunkIndex++)
        {
            var chunkEntities = new List<AuditEventEntity>(BatchSize);
            for (int j = 0; j < BatchSize; j++)
            {
                var rowIndex = chunkIndex * BatchSize + j;
                chunkEntities.Add(new AuditEventEntity
                {
                    EventType = $"Tamper.Chain.Event.{rowIndex}",
                    JsonData = $"{{\"index\":{rowIndex}}}",
                    User = stableUser,
                    UserId = stableUserId,
                    InsertedDate = insertedDate
                });
            }

            await using (var insertContext = CreateContext())
            {
                insertContext.AuditEvents.AddRange(chunkEntities);
                await insertContext.SaveChangesAsync();
            }

            foreach (var entity in chunkEntities)
            {
                allEventDtos.Add(new AuditIntegrityDto
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

        // Writer: one TamperDetectionService for all 10 batches so its _cachedPreviousHash
        // threads the chain across batches without re-querying the latest row each time.
        var writtenDtos = new List<AuditIntegrityDto>(TotalRows);
        await using (var writerContext = CreateContext())
        {
            var writerService = CreateService(writerContext);
            for (int batchStart = 0; batchStart < TotalRows; batchStart += BatchSize)
            {
                var batch = allEventDtos.GetRange(batchStart, BatchSize);
                var batchResult = await writerService.CreateIntegrityRecordBatchAsync(batch);
                writtenDtos.AddRange(batchResult);
            }
        }

        // Verifier: fresh context + service so paged reads aren't shadowed by the
        // writer-side identity map (which still tracks the 10k integrity entities).
        TamperDetectionResult result;
        await using (var verifyContext = CreateContext())
        {
            var verifyService = CreateService(verifyContext);
            result = await verifyService.VerifyChainIntegrityAsync(startDate: null, endDate: null);
        }

        Assert.Multiple(() =>
        {
            Assert.That(writtenDtos, Has.Count.EqualTo(TotalRows),
                "Writer should have produced one DTO per inserted event across all 10 batches.");
            Assert.That(result.IsValid, Is.True,
                "10k-row chain should verify clean.");
            Assert.That(result.ChainBroken, Is.False,
                "Chain continuity should be intact across all batch boundaries.");
            Assert.That(result.EventsChecked, Is.EqualTo(TotalRows),
                "Verifier should have visited every integrity row.");
            Assert.That(result.TotalEvents, Is.EqualTo(TotalRows),
                "Verifier's count query should match the row count.");
            Assert.That(result.TamperedEvents, Is.Empty,
                "No row should be flagged as tampered.");
        });
    }

    private static TamperDetectionService CreateService(AuditDbContext context)
    {
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var securityEventService = new Mock<IAuditSecurityEventService>().Object;

        return new TamperDetectionService(
            eventRepo,
            integrityRepo,
            securityEventService,
            NullLogger<TamperDetectionService>.Instance,
            IntegrityTestCrypto.Hasher,
            IntegrityTestCrypto.CreateHmacSigner(Encoding.UTF8.GetBytes(HmacKey), HmacKeyId));
    }
}
