using System.IO.Compression;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using MapsterMapper;
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
/// Tests for AuditArchivalService compression/decompression round-trips, hash verification,
/// deserialization failure paths, blob download errors, and audit event emission — all previously uncovered.
/// </summary>
[TestFixture]
[Category("Unit")]
public class AuditArchivalServiceCompressionTests
{
    private Mock<IAuditEventRepository> _mockAuditEventRepository;
    private Mock<IAuditIntegrityRepository> _mockAuditIntegrityRepository;
    private Mock<IArchiveRecordRepository> _mockArchiveRecordRepository;
    private Mock<ITamperDetectionService> _mockTamperDetectionService;
    private Mock<BlobServiceClient> _mockBlobServiceClient;
    private Mock<IMapper> _mockMapper;
    private Mock<ILogger<AuditArchivalService>> _mockLogger;
    private IConfiguration _configuration;

    /// <summary>
    /// Matches the serializer options used by the service to avoid JSON property name collisions.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockAuditIntegrityRepository = new Mock<IAuditIntegrityRepository>();
        _mockArchiveRecordRepository = new Mock<IArchiveRecordRepository>();
        _mockTamperDetectionService = new Mock<ITamperDetectionService>();
        _mockBlobServiceClient = new Mock<BlobServiceClient>();
        _mockMapper = new Mock<IMapper>();
        _mockLogger = new Mock<ILogger<AuditArchivalService>>();

        var configDict = new Dictionary<string, string>
        {
            ["Audit:Archive:ContainerName"] = "test-audit-archives"
        };

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict!)
            .Build();

        // StreamByEventIdsAsync is invoked on every archival path; default to an empty
        // async stream so individual tests don't have to wire it up unless they care.
        _mockAuditIntegrityRepository
            .Setup(static x => x.StreamByEventIdsAsync(
                It.IsAny<IReadOnlyList<Guid>>(),
                It.IsAny<CancellationToken>()))
            .Returns(static (IReadOnlyList<Guid> _, CancellationToken ct) =>
                ToAsyncEnumerable(Enumerable.Empty<AuditIntegrityEntity>(), ct));

        // Restore paths run inside ExecuteInTransactionAsync; without this default mock
        // the action would be swallowed and every restore test would spuriously pass.
        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());
    }

    private AuditArchivalService CreateService(
        ITamperDetectionService? tamperDetection = null,
        BlobServiceClient? blobClient = null)
    {
        return new AuditArchivalService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockArchiveRecordRepository.Object,
            _mockMapper.Object,
            _mockLogger.Object,
            _configuration,
            tamperDetection,
            blobClient);
    }

    /// <summary>
    /// Helper: GZip-compress a string the same way the service does.
    /// </summary>
    private static async Task<byte[]> CompressString(string data)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            await gzip.WriteAsync(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Helper: compute SHA-256 hash matching the service's ComputeBytesHash.
    /// </summary>
    private static string ComputeHash(byte[] data)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(sha.ComputeHash(data));
    }

    /// <summary>
    /// Helper: set up blob mocking chain that returns specific bytes when OpenReadAsync
    /// is called. Restore streams the blob through gzip decompression + a JSON state
    /// machine, so the mock hands back a MemoryStream over the compressed payload.
    /// </summary>
    private (Mock<BlobContainerClient>, Mock<BlobClient>) SetupBlobDownload(byte[] data, bool exists = true)
    {
        var mockContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainerClient.Object);

        mockContainerClient
            .Setup(static x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        mockBlobClient
            .Setup(static x => x.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(exists, null!));

        if (exists)
        {
            // Streaming restore uses OpenReadAsync → fresh Stream per call so retries work.
            mockBlobClient
                .Setup(static x => x.OpenReadAsync(
                    It.IsAny<BlobOpenReadOptions>(),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new MemoryStream(data, writable: false));

            // ValidateArchiveIntegrityAsync still uses DownloadToAsync into a caller stream.
            mockBlobClient
                .Setup(static x => x.DownloadToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
                .Callback<Stream, CancellationToken>((stream, _) => stream.Write(data, 0, data.Length))
                .ReturnsAsync(null! as Response);
        }

        return (mockContainerClient, mockBlobClient);
    }

    /// <summary>
    /// Helper: set up blob mocking chain for upload (archive path).
    /// </summary>
    private (Mock<BlobContainerClient>, Mock<BlobClient>) SetupBlobUpload()
    {
        var mockContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainerClient.Object);

        mockContainerClient
            .Setup(static x => x.CreateIfNotExistsAsync(
                It.IsAny<Azure.Storage.Blobs.Models.PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(null! as Response<BlobContainerInfo>);

        mockContainerClient
            .Setup(static x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        // Archival pipes bytes through a System.IO.Pipelines.Pipe whose reader side is
        // passed to UploadAsync. Drain it so the pipe writer doesn't block.
        mockBlobClient
            .Setup(static x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(static async (Stream s, bool _, CancellationToken ct) =>
            {
                var buf = new byte[8192];
                while (await s.ReadAsync(buf.AsMemory(0, buf.Length), ct) > 0) { }
                return null! as Response<BlobContentInfo>;
            });

        mockBlobClient
            .Setup(static x => x.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, null!));

        return (mockContainerClient, mockBlobClient);
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

    #region Restore — Full Round-Trip with Real Compression

    [Test]
    public async Task RestoreArchivedEventsAsync_WithValidCompressedArchive_RestoresSuccessfully()
    {
        // Arrange — build a real compressed archive blob
        var archiveId = "test-archive-1";
        var eventId = Guid.NewGuid();

        var archive = new AuditArchive
        {
            ArchiveId = archiveId,
            CreatedAt = DateTimeOffset.UtcNow,
            EventCount = 1,
            Events = new List<AuditEventDto>
            {
                new() { EventId = eventId, EventType = "Test.Event", InsertedDate = DateTimeOffset.UtcNow }
            },
            // Use WhenWritingNull to match service serialization behavior and avoid
            // AuditIntegrityDto.event_id property name collision during deserialization.
            // Note: the service deserializes without WhenWritingNull, so this is a known
            // limitation — archives with integrity records require matching serializer options.
            IntegrityRecords = new List<AuditIntegrityDto>()
        };

        var json = JsonSerializer.Serialize(archive, SerializerOptions);
        var compressed = await CompressString(json);
        var hash = ComputeHash(compressed);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "test.gz",
                ContainerName = "test-container",
                Hash = hash
            });

        SetupBlobDownload(compressed);

        _mockMapper
            .Setup(static x => x.Map<AuditEventEntity>(It.IsAny<AuditEventDto>()))
            .Returns(static (AuditEventDto dto) => new AuditEventEntity
            {
                EventId = dto.EventId ?? Guid.NewGuid(),
                EventType = dto.EventType
            });

        _mockMapper
            .Setup(static x => x.Map<AuditIntegrityEntity>(It.IsAny<AuditIntegrityDto>()))
            .Returns(static (AuditIntegrityDto dto) => new AuditIntegrityEntity
            {
                EventId = dto.EventId
            });

        _mockAuditEventRepository
            .Setup(static x => x.AddRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (IEnumerable<AuditEventEntity> e, CancellationToken _) => e);
        _mockAuditEventRepository
            .Setup(static x => x.ClearChangeTrackerAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAuditIntegrityRepository
            .Setup(static x => x.AddRangeAsync(It.IsAny<IEnumerable<AuditIntegrityEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (IEnumerable<AuditIntegrityEntity> e, CancellationToken _) => e);
        _mockAuditIntegrityRepository
            .Setup(static x => x.ClearChangeTrackerAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockAuditIntegrityRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert
        Assert.That(result.Success, Is.True);
        Assert.That(result.RestoredEventCount, Is.EqualTo(1));
        Assert.That(result.Message, Does.Contain("Successfully restored"));

        _mockAuditEventRepository.Verify(
            static x => x.AddRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Restore — Hash Mismatch

    [Test]
    public async Task RestoreArchivedEventsAsync_WithHashMismatch_ReturnsIntegrityFailure()
    {
        // Arrange — valid compressed archive but the stored hash doesn't match
        var archiveId = "hash-mismatch-test";
        var archive = new AuditArchive
        {
            ArchiveId = archiveId,
            Events = new List<AuditEventDto>(),
            IntegrityRecords = new List<AuditIntegrityDto>()
        };

        var json = JsonSerializer.Serialize(archive, SerializerOptions);
        var compressed = await CompressString(json);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "test.gz",
                ContainerName = "test-container",
                Hash = "deliberately-wrong-hash"
            });

        SetupBlobDownload(compressed);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("integrity check failed"));

        _mockArchiveRecordRepository.Verify(
            x => x.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                "Hash verification failed", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Restore — Deserialization Failure

    [Test]
    public async Task RestoreArchivedEventsAsync_WithInvalidJson_ReturnsDeserializationError()
    {
        // Arrange — compressed data that decompresses to invalid JSON
        var archiveId = "bad-json-test";
        var invalidJson = "this is not valid JSON {{{";
        var compressed = await CompressString(invalidJson);
        var hash = ComputeHash(compressed);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "bad.gz",
                ContainerName = "test-container",
                Hash = hash
            });

        SetupBlobDownload(compressed);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert — should fail gracefully (either deserialization returns null or throws)
        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task RestoreArchivedEventsAsync_WithNullDeserializedArchive_ReturnsError()
    {
        // Arrange — JSON "null" is a malformed archive (top level must be an object).
        // The streaming parser raises a JsonException which the service catches and
        // surfaces through the Restore failure path.
        var archiveId = "null-archive-test";
        var nullJson = "null";
        var compressed = await CompressString(nullJson);
        var hash = ComputeHash(compressed);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "null.gz",
                ContainerName = "test-container",
                Hash = hash
            });

        SetupBlobDownload(compressed);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert — malformed archive, service reports the error gracefully
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Restore failed"));
    }

    #endregion

    #region Restore — Corrupted Compressed Data

    [Test]
    public async Task RestoreArchivedEventsAsync_WithCorruptedCompressedData_ReturnsError()
    {
        // Arrange — bytes that are not valid GZip
        var archiveId = "corrupt-gz-test";
        var corruptedData = new byte[] { 0x00, 0x01, 0x02, 0xFF, 0xFE };
        var hash = ComputeHash(corruptedData);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "corrupt.gz",
                ContainerName = "test-container",
                Hash = hash
            });

        SetupBlobDownload(corruptedData);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert — GZip decompression should fail and be caught by the outer catch
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Restore failed"));
    }

    #endregion

    #region Validate — Hash Mismatch

    [Test]
    public async Task ValidateArchiveIntegrityAsync_WithHashMismatch_ReturnsFalseAndMarksCorrupted()
    {
        // Arrange
        var archiveId = "validate-mismatch-test";
        var someData = await CompressString("some archive data");

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "archive.gz",
                ContainerName = "test-container",
                Hash = "wrong-hash-value"
            });

        SetupBlobDownload(someData);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var isValid = await service.ValidateArchiveIntegrityAsync(archiveId);

        // Assert
        Assert.That(isValid, Is.False);

        _mockArchiveRecordRepository.Verify(
            x => x.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                "Hash verification failed", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task ValidateArchiveIntegrityAsync_WithValidHash_ReturnsTrueAndUpdatesTimestamp()
    {
        // Arrange
        var archiveId = "validate-valid-test";
        var someData = await CompressString("valid archive data");
        var correctHash = ComputeHash(someData);

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "archive.gz",
                ContainerName = "test-container",
                Hash = correctHash
            });

        SetupBlobDownload(someData);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateVerificationTimestampAsync(
                It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var isValid = await service.ValidateArchiveIntegrityAsync(archiveId);

        // Assert
        Assert.That(isValid, Is.True);

        _mockArchiveRecordRepository.Verify(
            x => x.UpdateVerificationTimestampAsync(
                archiveId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Validate — Blob Download Failure

    [Test]
    public async Task ValidateArchiveIntegrityAsync_WhenDownloadThrows_ReturnsFalse()
    {
        // Arrange
        var archiveId = "download-fail-test";

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "archive.gz",
                ContainerName = "test-container",
                Hash = "some-hash"
            });

        var mockContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainerClient.Object);

        mockContainerClient
            .Setup(static x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        mockBlobClient
            .Setup(static x => x.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, null!));

        mockBlobClient
            .Setup(static x => x.DownloadToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("Storage unavailable"));

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var isValid = await service.ValidateArchiveIntegrityAsync(archiveId);

        // Assert — caught by outer try/catch, returns false
        Assert.That(isValid, Is.False);
    }

    #endregion

    #region Archive — Audit Event Emission Failure

    [Test]
    public async Task ArchiveAuditEventsAsync_WhenAuditEventEmissionFails_StillReturnsSuccess()
    {
        // Arrange — set up a complete successful archive, but make the audit event creation fail
        var archiveBefore = DateTimeOffset.UtcNow.AddDays(-90);
        var eventId = Guid.NewGuid();

        var events = new List<AuditEventDto>
        {
            new()
            {
                EventId = eventId,
                EventType = "Test.Event",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-100)
            }
        };

        // Repository setup
        _mockArchiveRecordRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditArchiveRecordEntity e, CancellationToken _) => e);

        _mockArchiveRecordRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditArchiveRecordEntity e, CancellationToken _) => e);

        var entities = new List<AuditEventEntity>
        {
            new()
            {
                EventId = eventId,
                EventType = "Test.Event",
                InsertedDate = DateTimeOffset.UtcNow.AddDays(-100)
            }
        };

        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities.Count);

        _mockAuditEventRepository
            .Setup(x => x.StreamByDateAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<AuditEventEntity, bool>> _, CancellationToken ct) =>
                ToAsyncEnumerable(entities, ct));

        _mockMapper
            .Setup(static x => x.Map<AuditEventDto>(It.IsAny<AuditEventEntity>()))
            .Returns((AuditEventEntity e) => new AuditEventDto
            {
                EventId = e.EventId,
                EventType = e.EventType,
                InsertedDate = e.InsertedDate
            });

        _mockTamperDetectionService
            .Setup(static x => x.VerifyIntegrityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns(static (Func<Task> action, CancellationToken _) => action());

        _mockAuditEventRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockAuditEventRepository
            .Setup(static x => x.ExecuteDeleteWhereAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities.Count);

        _mockAuditIntegrityRepository
            .Setup(static x => x.ExecuteDeleteWhereAsync(
                It.IsAny<Expression<Func<AuditIntegrityEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        SetupBlobUpload();

        // Make the post-archive audit event emission fail
        var addCallCount = 0;
        _mockAuditEventRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback(() => addCallCount++)
            .Returns((AuditEventEntity e, CancellationToken _) =>
            {
                // First call might be during transaction; the audit emission call throws
                if (addCallCount > 0 && e.EventType == "Audit.Archived")
                    throw new InvalidOperationException("DB write failed for audit event");
                return Task.FromResult(e);
            });

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.ArchiveAuditEventsAsync(archiveBefore, "test-archive");

        // Assert — archive itself should succeed despite audit event emission failure
        Assert.That(result.Success, Is.True);
        Assert.That(result.EventCount, Is.EqualTo(1));
    }

    #endregion

    #region Restore — Blob Download Exception

    [Test]
    public async Task RestoreArchivedEventsAsync_WhenDownloadThrows_ReturnsFailure()
    {
        // Arrange
        var archiveId = "download-error-test";

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "archive.gz",
                ContainerName = "test-container",
                Hash = "some-hash"
            });

        var mockContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainerClient.Object);

        mockContainerClient
            .Setup(static x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        mockBlobClient
            .Setup(static x => x.ExistsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, null!));

        mockBlobClient
            .Setup(static x => x.OpenReadAsync(
                It.IsAny<BlobOpenReadOptions>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("Network error"));

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.RestoreArchivedEventsAsync(archiveId);

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Restore failed"));
    }

    #endregion

    #region Archive — Blob Upload Failure

    [Test]
    public async Task ArchiveAuditEventsAsync_WhenUploadThrows_ReturnsFailure()
    {
        // Arrange
        var archiveBefore = DateTimeOffset.UtcNow.AddDays(-90);
        var eventId = Guid.NewGuid();

        _mockArchiveRecordRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditArchiveRecordEntity e, CancellationToken _) => e);

        _mockArchiveRecordRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var entities = new List<AuditEventEntity>
        {
            new() { EventId = eventId, EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-100) }
        };

        _mockAuditEventRepository
            .Setup(static x => x.CountAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(entities.Count);

        _mockAuditEventRepository
            .Setup(x => x.StreamByDateAsync(
                It.IsAny<Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .Returns((Expression<Func<AuditEventEntity, bool>> _, CancellationToken ct) =>
                ToAsyncEnumerable(entities, ct));

        _mockMapper
            .Setup(static x => x.Map<AuditEventDto>(It.IsAny<AuditEventEntity>()))
            .Returns((AuditEventEntity e) => new AuditEventDto
            {
                EventId = e.EventId,
                EventType = e.EventType,
                InsertedDate = e.InsertedDate
            });

        _mockTamperDetectionService
            .Setup(static x => x.VerifyIntegrityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Set up blob client to throw on upload
        var mockContainerClient = new Mock<BlobContainerClient>();
        var mockBlobClient = new Mock<BlobClient>();

        _mockBlobServiceClient
            .Setup(static x => x.GetBlobContainerClient(It.IsAny<string>()))
            .Returns(mockContainerClient.Object);

        mockContainerClient
            .Setup(static x => x.CreateIfNotExistsAsync(
                It.IsAny<Azure.Storage.Blobs.Models.PublicAccessType>(),
                It.IsAny<IDictionary<string, string>>(),
                It.IsAny<BlobContainerEncryptionScopeOptions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(null! as Response<BlobContainerInfo>);

        mockContainerClient
            .Setup(static x => x.GetBlobClient(It.IsAny<string>()))
            .Returns(mockBlobClient.Object);

        mockBlobClient
            .Setup(static x => x.UploadAsync(It.IsAny<Stream>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RequestFailedException("Storage quota exceeded"));

        mockBlobClient
            .Setup(static x => x.DeleteIfExistsAsync(
                It.IsAny<DeleteSnapshotsOption>(),
                It.IsAny<BlobRequestConditions>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Response.FromValue(true, null!));

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var result = await service.ArchiveAuditEventsAsync(archiveBefore, "upload-fail-archive");

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Storage quota exceeded"));

        // Archive record should be marked as failed
        _mockArchiveRecordRepository.Verify(
            static x => x.UpdateStatusAsync(It.IsAny<string>(), MillWorksArchiveStatus.Failed,
                It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Validate — Blob Missing

    [Test]
    public async Task ValidateArchiveIntegrityAsync_WithMissingBlob_ReturnsFalseAndMarksCorrupted()
    {
        // Arrange
        var archiveId = "missing-blob-validate";

        _mockArchiveRecordRepository
            .Setup(x => x.GetByArchiveIdAsync(archiveId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditArchiveRecordEntity
            {
                ArchiveId = archiveId,
                Status = MillWorksArchiveStatus.Completed,
                BlobName = "missing.gz",
                ContainerName = "test-container",
                Hash = "some-hash"
            });

        SetupBlobDownload(Array.Empty<byte>(), exists: false);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var service = CreateService(
            _mockTamperDetectionService.Object,
            _mockBlobServiceClient.Object);

        // Act
        var isValid = await service.ValidateArchiveIntegrityAsync(archiveId);

        // Assert
        Assert.That(isValid, Is.False);

        _mockArchiveRecordRepository.Verify(
            x => x.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                "Blob file missing", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Archive — No Blob Service Client

    [Test]
    public async Task ArchiveAuditEventsAsync_WithNoBlobServiceClient_ReturnsFailure()
    {
        // Arrange — service created without blob client
        var archiveBefore = DateTimeOffset.UtcNow.AddDays(-90);
        var eventId = Guid.NewGuid();

        _mockArchiveRecordRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditArchiveRecordEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditArchiveRecordEntity e, CancellationToken _) => e);

        _mockArchiveRecordRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockArchiveRecordRepository
            .Setup(static x => x.UpdateStatusAsync(It.IsAny<string>(), It.IsAny<MillWorksArchiveStatus>(),
                It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _mockAuditEventRepository
            .Setup(static x => x.FindAsync(It.IsAny<System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditEventEntity>
            {
                new() { EventId = eventId, EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-100) }
            });

        _mockMapper
            .Setup(static x => x.Map<IEnumerable<AuditEventDto>>(It.IsAny<object>()))
            .Returns(new List<AuditEventDto>
            {
                new() { EventId = eventId, EventType = "Test", InsertedDate = DateTimeOffset.UtcNow.AddDays(-100) }
            });

        _mockMapper
            .Setup(static x => x.Map<List<AuditEventEntity>>(It.IsAny<object>()))
            .Returns(new List<AuditEventEntity>
            {
                new() { EventId = eventId, EventType = "Test" }
            });

        _mockMapper
            .Setup(static x => x.Map<List<AuditIntegrityDto>>(It.IsAny<object>()))
            .Returns(new List<AuditIntegrityDto>());

        _mockAuditIntegrityRepository
            .Setup(static x => x.GetByEventIdAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditIntegrityEntity?)null);

        _mockTamperDetectionService
            .Setup(static x => x.VerifyIntegrityAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // No blob client — pass null
        var service = CreateService(
            _mockTamperDetectionService.Object,
            blobClient: null);

        // Act
        var result = await service.ArchiveAuditEventsAsync(archiveBefore, "no-blob-archive");

        // Assert
        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Blob storage client is not configured"));
    }

    #endregion
}
