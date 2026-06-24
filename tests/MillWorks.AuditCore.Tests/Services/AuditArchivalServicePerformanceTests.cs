using System.Diagnostics;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Phase 5: Performance and soak tests for AuditArchivalService (TestPlanPhaseFive §6).
/// Establishes baselines for archive creation wall-clock time, memory usage, transaction
/// duration, and concurrent read behavior. Uses mocked repositories and blob storage so
/// results reflect the service's own overhead, not storage I/O.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class AuditArchivalServicePerformanceTests
{
    private Mock<IAuditEventRepository> _mockAuditEventRepository = null!;
    private Mock<IAuditIntegrityRepository> _mockAuditIntegrityRepository = null!;
    private Mock<IArchiveRecordRepository> _mockArchiveRecordRepository = null!;
    private Mock<ITamperDetectionService> _mockTamperDetectionService = null!;
    private Mock<BlobServiceClient> _mockBlobServiceClient = null!;
    private Mock<ILogger<AuditArchivalService>> _mockLogger = null!;
    private IConfiguration _configuration = null!;

    private long _lastUploadByteCount;
    private TimeSpan _lastTransactionDuration;

    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockAuditIntegrityRepository = new Mock<IAuditIntegrityRepository>();
        _mockArchiveRecordRepository = new Mock<IArchiveRecordRepository>();
        _mockTamperDetectionService = new Mock<ITamperDetectionService>();
        _mockBlobServiceClient = new Mock<BlobServiceClient>();
        _mockLogger = new Mock<ILogger<AuditArchivalService>>();

        _lastUploadByteCount = 0;
        _lastTransactionDuration = TimeSpan.Zero;

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string>
            {
                ["Audit:Archive:ContainerName"] = "perf-test-archives"
            }!)
            .Build();
    }

    // ── §6 Archival: 10K events wall-clock + memory + transaction duration ──

    [Test]
    [CancelAfter(120000)]
    public async Task Archive_10000Events_MeasuresWallClockMemoryAndTransactionDuration()
    {
        const int eventCount = 10_000;
        SeedArchiveWorkload(eventCount, jsonDataBytesPerEvent: 256);
        var service = CreateService();

        var memBefore = GC.GetTotalMemory(forceFullCollection: true);
        var sw = Stopwatch.StartNew();
        var result = await service.ArchiveAuditEventsAsync(
            DateTimeOffset.UtcNow.AddDays(-1),
            archiveId: null,
            CancellationToken.None);
        sw.Stop();
        var memAfter = GC.GetTotalMemory(forceFullCollection: false);

        var peakAllocatedMb = Math.Max(0, memAfter - memBefore) / 1024.0 / 1024.0;

        TestContext.Out.WriteLine(
            $"Archive 10K events: wall={sw.ElapsedMilliseconds}ms, "
            + $"txn={_lastTransactionDuration.TotalMilliseconds:F0}ms, "
            + $"uploadSize={_lastUploadByteCount / 1024.0:F0}KB, "
            + $"netAlloc={peakAllocatedMb:F1}MB");

        result.Success.Should().BeTrue("archival of 10K events should succeed");
        result.EventCount.Should().Be(eventCount);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60),
            "archival of 10K events should complete within a minute on CI hardware");
        _lastTransactionDuration.Should().BeLessThan(TimeSpan.FromSeconds(5),
            "no single archival transaction should hold DB locks longer than 5s");
    }

    // ── §6 Archival: concurrent reads during archive → no deadlock ──

    [Test]
    [CancelAfter(60000)]
    public async Task Archive_WithConcurrentReads_CompletesWithoutDeadlock()
    {
        const int eventCount = 1_000;
        SeedArchiveWorkload(eventCount, jsonDataBytesPerEvent: 128);
        var service = CreateService();

        // Simulate active read traffic: callers enumerating historical archives while
        // archival is running. GetArchivesAsync is the read path exposed to consumers.
        _mockArchiveRecordRepository
            .Setup(static x => x.GetAllOrderedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditArchiveRecordEntity>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var archiveTask = Task.Run(
            () => service.ArchiveAuditEventsAsync(DateTimeOffset.UtcNow.AddDays(-1), null, cts.Token),
            cts.Token);

        var readerTasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!archiveTask.IsCompleted && !cts.Token.IsCancellationRequested)
            {
                await service.GetArchivesAsync(cts.Token);
            }
        }, cts.Token)).ToArray();

        var act = async () =>
        {
            var archiveResult = await archiveTask;
            await Task.WhenAll(readerTasks);
            return archiveResult;
        };

        var result = await act.Should().NotThrowAsync("concurrent archive + reads must not deadlock");
        result.Subject.Success.Should().BeTrue();
    }

    // ── §6 Archival: large JsonData (10KB / event) → verify streaming, bounded memory ──

    [Test]
    [CancelAfter(120000)]
    public async Task Archive_With10KbJsonDataPerEvent_KeepsMemoryBounded()
    {
        // Plan §6 requires "streaming, not full materialization." With the streaming
        // pipeline (AsAsyncEnumerable → JSON writer → GZip → block-blob OpenWrite), peak
        // managed allocation should stay close to the compressed payload plus the event-id
        // list, independent of raw event size.
        const int eventCount = 1_000;
        const int jsonBytesPerEvent = 10 * 1024;
        const long totalJsonBytes = (long)eventCount * jsonBytesPerEvent;

        SeedArchiveWorkload(eventCount, jsonDataBytesPerEvent: jsonBytesPerEvent);
        var service = CreateService();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var memBefore = GC.GetTotalMemory(forceFullCollection: true);

        var result = await service.ArchiveAuditEventsAsync(
            DateTimeOffset.UtcNow.AddDays(-1), null, CancellationToken.None);

        var memAfter = GC.GetTotalMemory(forceFullCollection: false);
        var netAllocMb = Math.Max(0, memAfter - memBefore) / 1024.0 / 1024.0;
        var totalDataMb = totalJsonBytes / 1024.0 / 1024.0;

        TestContext.Out.WriteLine(
            $"Archive 1K events × 10KB JsonData: totalData={totalDataMb:F1}MB, "
            + $"netAlloc={netAllocMb:F1}MB, "
            + $"ratio={(totalDataMb > 0 ? netAllocMb / totalDataMb : 0):F2}x, "
            + $"compressedUploadSize={_lastUploadByteCount / 1024.0:F0}KB");

        result.Success.Should().BeTrue();
        result.EventCount.Should().Be(eventCount);

        // Streaming guardrail: net allocation should stay well below the raw data size.
        // The previous full-materialization path allocated ~5× raw; streaming compresses
        // high-repeat test data to well under 1× raw in memory. A 1.5× cap catches any
        // regression back toward full materialization.
        netAllocMb.Should().BeLessThan(totalDataMb * 1.5,
            "streaming archival should allocate well below raw event size");
    }

    #region Helpers

    private AuditArchivalService CreateService(bool withTamperDetection = true)
    {
        return new AuditArchivalService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockArchiveRecordRepository.Object,
            _mockLogger.Object,
            _configuration,
            withTamperDetection ? _mockTamperDetectionService.Object : null,
            _mockBlobServiceClient.Object);
    }

    /// <summary>
    /// Seed the mock pipeline so a full archive call succeeds end-to-end for the
    /// requested event count. Generates deterministic synthetic events with the
    /// requested JsonData byte size per event.
    /// </summary>
    private void SeedArchiveWorkload(int eventCount, int jsonDataBytesPerEvent)
    {
        var eventEntities = new List<AuditEventEntity>(eventCount);
        for (var i = 0; i < eventCount; i++)
        {
            var id = Guid.NewGuid();
            var ts = DateTimeOffset.UtcNow.AddDays(-30).AddSeconds(i);
            var jsonData = jsonDataBytesPerEvent > 0
                ? "{\"payload\":\"" + new string('x', Math.Max(0, jsonDataBytesPerEvent - 14)) + "\"}"
                : null;

            eventEntities.Add(new AuditEventEntity
            {
                EventId = id,
                EventType = "Perf.Event",
                InsertedDate = ts,
                User = "perf",
                EntityType = "PerfEntity",
                Action = "Created",
                JsonData = jsonData
            });
        }

        _mockAuditEventRepository
            .Setup(x => x.CountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventCount);

        _mockAuditEventRepository
            .Setup(x => x.StreamByDateAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<AuditEventEntity, bool>> _, CancellationToken ct) =>
                ToAsyncEnumerable(eventEntities, ct));

        _mockAuditEventRepository
            .Setup(x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(async (Func<Task> action, CancellationToken _) =>
            {
                var txSw = Stopwatch.StartNew();
                await action();
                txSw.Stop();
                _lastTransactionDuration = txSw.Elapsed;
            });

        _mockAuditEventRepository
            .Setup(x => x.ExecuteDeleteWhereAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventCount);
        _mockAuditEventRepository
            .Setup(x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        _mockAuditEventRepository
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventCount);

        _mockAuditIntegrityRepository
            .Setup(x => x.StreamByEventIdsAsync(
                It.IsAny<IReadOnlyList<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyList<Guid> _, CancellationToken ct) =>
                ToAsyncEnumerable(Enumerable.Empty<AuditIntegrityEntity>(), ct));
        _mockAuditIntegrityRepository
            .Setup(x => x.ExecuteDeleteWhereAsync(
                It.IsAny<Expression<Func<AuditIntegrityEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _mockAuditIntegrityRepository
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditArchiveRecordEntity?)null);
        _mockArchiveRecordRepository
            .Setup(x => x.AddAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditArchiveRecordEntity e, CancellationToken _) => e);
        _mockArchiveRecordRepository
            .Setup(x => x.UpdateAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditArchiveRecordEntity e, CancellationToken _) => e);
        _mockArchiveRecordRepository
            .Setup(x => x.UpdateStatusAsync(
                It.IsAny<string>(),
                It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockArchiveRecordRepository
            .Setup(x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockTamperDetectionService
            .Setup(x => x.VerifyIntegrityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        SetupBlobUploadCapture();
    }

    private void SetupBlobUploadCapture()
    {
        var mockContainer = new Mock<BlobContainerClient>();
        var mockBlob = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainer.Object);

        mockContainer
            .Setup(x => x.CreateIfNotExistsAsync(
                It.IsAny<PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(null! as Response<BlobContainerInfo>);

        mockContainer
            .Setup(x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlob.Object);

        // Archival pipes compressed bytes through a System.IO.Pipelines.Pipe whose reader
        // side is passed to UploadAsync. The mock drains the stream into a byte counter
        // (not a MemoryStream) so the test measures the service's own allocation, not the
        // captured payload.
        mockBlob
            .Setup(x => x.UploadAsync(
                It.IsAny<Stream>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (Stream stream, bool _, CancellationToken ct) =>
            {
                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
                {
                    total += read;
                }

                _lastUploadByteCount = total;
                return null! as Response<BlobContentInfo>;
            });
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(
        IEnumerable<T> source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var item in source)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return item;
            await Task.Yield();
        }
    }

    #endregion
}
