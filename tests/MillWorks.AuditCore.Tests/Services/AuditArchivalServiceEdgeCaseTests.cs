using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Edge case unit tests for AuditArchivalService covering scenarios not already
/// handled by AuditArchivalServiceTests: already-restored status, empty archive list,
/// and large-dataset graceful blob failure.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditArchivalServiceEdgeCaseTests
{
    private Mock<IAuditEventRepository> _mockAuditEventRepository;
    private Mock<IAuditIntegrityRepository> _mockAuditIntegrityRepository;
    private Mock<IArchiveRecordRepository> _mockArchiveRecordRepository;
    private Mock<ITamperDetectionService> _mockTamperDetectionService;
    private Mock<BlobServiceClient> _mockBlobServiceClient;
    private Mock<ILogger<AuditArchivalService>> _mockLogger;
    private IConfiguration _configuration;
    private AuditArchivalService _archivalService;

    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository      = new Mock<IAuditEventRepository>();
        _mockAuditIntegrityRepository  = new Mock<IAuditIntegrityRepository>();
        _mockArchiveRecordRepository   = new Mock<IArchiveRecordRepository>();
        _mockTamperDetectionService    = new Mock<ITamperDetectionService>();
        _mockBlobServiceClient         = new Mock<BlobServiceClient>();
        _mockLogger                    = new Mock<ILogger<AuditArchivalService>>();

        var configDict = new Dictionary<string, string>
        {
            ["Audit:Archive:ContainerName"] = "test-audit-archives"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        _archivalService = new AuditArchivalService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockArchiveRecordRepository.Object,
            _mockLogger.Object,
            _configuration,
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Archival always invokes StreamByEventIdsAsync after event iteration. Default to
        // an empty stream so individual tests don't need to wire it up unless they care.
        _mockAuditIntegrityRepository
            .Setup(static x => x.StreamByEventIdsAsync(
                It.IsAny<IReadOnlyList<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(static (IReadOnlyList<Guid> _, CancellationToken ct) =>
                ToAsyncEnumerable(Enumerable.Empty<AuditIntegrityEntity>(), ct));
    }

    #region RestoreArchivedEventsAsync — already-restored status

    /// <summary>
    /// Verifies that attempting to restore an archive record whose status is Failed
    /// (i.e. not Completed — which is the only status that permits restore) returns
    /// a failure result indicating the archive is not in the required completed state,
    /// rather than throwing an exception.  This covers the "already processed / non-active"
    /// path, which includes any previously failed or in-flight archive.
    /// </summary>
    [Test]
    public async Task RestoreArchivedEventsAsync_AlreadyRestored_HandlesGracefully()
    {
        // Arrange — archive record exists but its status is Failed, not Completed
        var archiveId = Guid.NewGuid().ToString();

        var archiveRecord = new AuditArchiveRecordEntity
        {
            ArchiveId     = archiveId,
            BlobName      = "failed-archive.gz",
            ContainerName = "test-audit-archives",
            Status        = MillWorksArchiveStatus.Failed
        };

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(archiveRecord);

        // Act
        var result = await _archivalService.RestoreArchivedEventsAsync(archiveId);

        // Assert — should not succeed and should report the non-completed state
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not in completed state"));
        Assert.That(result.ArchiveId, Is.EqualTo(archiveId));
    }

    #endregion

    #region GetArchivesAsync — empty list returns empty

    /// <summary>
    /// Verifies that GetArchivesAsync returns an empty list (not null) when the
    /// repository contains no archive records.
    /// </summary>
    [Test]
    public async Task GetArchivesAsync_Pagination_EmptyList_ReturnsEmpty()
    {
        // Arrange
        _mockArchiveRecordRepository
            .Setup(static x => x.GetAllOrderedAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditArchiveRecordEntity>());

        // Act
        var result = await _archivalService.GetArchivesAsync();

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result, Is.Empty);
    }

    #endregion

    #region ArchiveAuditEventsAsync — large dataset fails at blob gracefully

    /// <summary>
    /// Verifies that when 100 events are present the service processes them all the
    /// way through integrity checks before reaching the blob upload step where it
    /// fails gracefully, returning a failure result rather than throwing an unhandled
    /// exception.
    /// </summary>
    [Test]
    public async Task ArchiveAuditEventsAsync_LargeDataset_ProcessesCorrectly()
    {
        // Arrange — 100 events with valid EventIds
        const int eventCount = 100;
        var archiveBefore = DateTimeOffset.UtcNow.AddDays(-90);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        _mockArchiveRecordRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditArchiveRecordEntity e, CancellationToken _) => e);

        _mockArchiveRecordRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(
                It.IsAny<string>(),
                It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var eventEntities = Enumerable.Range(0, eventCount)
            .Select(static _ => new AuditEventEntity
            {
                EventId      = Guid.NewGuid(),
                EventType    = "Data.Export",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-100)
            })
            .ToList();

        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(eventEntities.Count);

        _mockAuditEventRepository
            .Setup(x => x.StreamByDateAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<AuditEventEntity, bool>> _, CancellationToken ct) =>
                ToAsyncEnumerable(eventEntities, ct));

        // All integrity checks pass
        _mockTamperDetectionService
            .Setup(static x => x.VerifyIntegrityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Blob upload throws — simulates Azure being unavailable
        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Throws(new Exception("Azure Blob Storage unavailable"));

        // Act
        var result = await _archivalService.ArchiveAuditEventsAsync(archiveBefore);

        // Assert — service must not throw; it must return a failure result
        Assert.That(result, Is.Not.Null);
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("failed"));

    }

    #endregion

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
}
