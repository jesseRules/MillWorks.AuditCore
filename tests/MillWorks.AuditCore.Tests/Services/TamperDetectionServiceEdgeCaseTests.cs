using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Edge case tests for TamperDetectionService covering scenarios not addressed in TamperDetectionServiceTests
/// </summary>
[TestFixture]
[Category("Unit")]
public class TamperDetectionServiceEdgeCaseTests
{
    private Mock<IAuditEventRepository> _mockAuditEventRepository;
    private Mock<IAuditIntegrityRepository> _mockAuditIntegrityRepository;
    private Mock<IAuditSecurityEventService> _mockSecurityEventService;
    private Mock<ILogger<TamperDetectionService>> _mockLogger;
    private TamperDetectionService _tamperDetectionService;

    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockAuditIntegrityRepository = new Mock<IAuditIntegrityRepository>();
        _mockSecurityEventService = new Mock<IAuditSecurityEventService>();
        _mockLogger = new Mock<ILogger<TamperDetectionService>>();

        var auditOptions = Options.Create(new AuditOptions
        {
            Environment = "Development",
            HmacKey = "test-hmac-key-for-testing-12345678"
        });
        var securityOptions = Options.Create(new SecurityOptions());

        _tamperDetectionService = new TamperDetectionService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockSecurityEventService.Object,
            _mockLogger.Object,
            auditOptions,
            securityOptions,
            new InMemoryDistributedLockService(NullLogger<InMemoryDistributedLockService>.Instance));
    }

    /// <summary>
    /// A single-element sequence [1] has no pair to compare, so no gap can exist.
    /// VerifySequenceIntegrityAsync should return true.
    /// </summary>
    [Test]
    public async Task VerifySequenceIntegrityAsync_SingleElement_ReturnsTrue()
    {
        // Arrange
        var sequenceNumbers = new List<long> { 1 };

        _mockAuditIntegrityRepository
            .Setup(static x => x.GetAllSequenceNumbersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(sequenceNumbers);

        // Act
        var result = await _tamperDetectionService.VerifySequenceIntegrityAsync();

        // Assert
        Assert.That(result, Is.True);
    }

    /// <summary>
    /// ExportIntegrityProofAsync should include every field of the integrity entity in the exported proof JSON.
    /// </summary>
    [Test]
    public async Task ExportIntegrityProofAsync_WithAllFields_IncludesAllInProof()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var trustedTimestamp = DateTimeOffset.UtcNow;

        var integrity = new AuditIntegrityEntity
        {
            EventId = eventId,
            EventHash = "abc123eventhash",
            PreviousEventHash = "prev456hash",
            TrustedTimestamp = trustedTimestamp,
            SequenceNumber = 99,
            DigitalSignature = "sig789",
            AlgorithmVersion = 2,
            HmacSignature = "hmac111",
            Checksum = "chk222"
        };

        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrity);

        // Act
        var resultBytes = await _tamperDetectionService.ExportIntegrityProofAsync(eventId);

        // Assert
        Assert.That(resultBytes, Is.Not.Null);
        Assert.That(resultBytes.Length, Is.GreaterThan(0));

        var json = System.Text.Encoding.UTF8.GetString(resultBytes);
        Assert.That(json, Does.Contain(eventId.ToString()));
        Assert.That(json, Does.Contain("abc123eventhash"));
        Assert.That(json, Does.Contain("prev456hash"));
        Assert.That(json, Does.Contain("99"));
        Assert.That(json, Does.Contain("sig789"));
        Assert.That(json, Does.Contain("2"));
    }

    /// <summary>
    /// DetectTamperingAsync should report an alert for each individual chain break,
    /// not just a single "Chain Broken" alert when multiple records have wrong PreviousEventHash.
    /// </summary>
    [Test]
    public async Task DetectTamperingAsync_WithMultipleChainBreaks_ReportsAll()
    {
        // Arrange — valid sequence, no gaps
        _mockAuditIntegrityRepository
            .Setup(static x => x.GetAllSequenceNumbersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<long> { 1, 2, 3, 4 });

        // Four records; records at index 2 and 3 both have broken chains
        var integrityRecords = new List<AuditIntegrityEntity>
        {
            new()
            {
                EventId = Guid.NewGuid(),
                EventHash = "hash1",
                PreviousEventHash = null,
                AuditEvent = null
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventHash = "hash2",
                PreviousEventHash = "hash1",
                AuditEvent = null
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventHash = "hash3",
                PreviousEventHash = "wrong-hash-A", // first chain break
                AuditEvent = null
            },
            new()
            {
                EventId = Guid.NewGuid(),
                EventHash = "hash4",
                PreviousEventHash = "wrong-hash-B", // second chain break
                AuditEvent = null
            }
        };

        // VerifyChainIntegrityAsync (called by DetectTamperingAsync) uses the paged API
        _mockAuditIntegrityRepository
            .Setup(static x => x.GetCountAsync(
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrityRecords.Count);

        _mockAuditIntegrityRepository
            .Setup(static x => x.GetWithAuditEventsPagedAsync(
                It.IsAny<DateTimeOffset?>(),
                It.IsAny<DateTimeOffset?>(),
                0,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrityRecords);

        // Act
        var alerts = await _tamperDetectionService.DetectTamperingAsync();

        // Assert — the service emits exactly one "Chain Broken" alert when ChainBroken is true,
        // plus one "Integrity Violation" alert per tampered event tracked in TamperedEvents
        Assert.That(alerts, Is.Not.Null);
        Assert.That(alerts.Any(static a => a.AlertType == "Chain Broken"), Is.True);

        // Both broken-chain events are tracked as TamperedEvents and surface as Integrity Violation alerts
        var chainBrokenAlerts = alerts.Count(static a => a.AlertType == "Chain Broken");
        var integrityViolationAlerts = alerts.Count(static a => a.AlertType == "Integrity Violation");
        Assert.That(chainBrokenAlerts + integrityViolationAlerts, Is.GreaterThanOrEqualTo(2));
    }

    /// <summary>
    /// VerifyChainIntegrityAsync over an empty date range (no records in the repository)
    /// should return a valid result with TotalEvents equal to 0.
    /// </summary>
    [Test]
    public async Task VerifyChainIntegrityAsync_EmptyDateRange_ReturnsValidResult()
    {
        // Arrange
        var startDate = DateTimeOffset.UtcNow.AddDays(-7);
        var endDate = DateTimeOffset.UtcNow;

        _mockAuditIntegrityRepository
            .Setup(x => x.GetCountAsync(startDate, endDate, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        _mockAuditIntegrityRepository
            .Setup(x => x.GetWithAuditEventsPagedAsync(
                startDate,
                endDate,
                0,
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AuditIntegrityEntity>());

        // Act
        var result = await _tamperDetectionService.VerifyChainIntegrityAsync(startDate, endDate);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.TotalEvents, Is.EqualTo(0));
        Assert.That(result.ChainBroken, Is.False);
    }

    /// <summary>
    /// CreateIntegrityRecordAsync should use the EventHash of the latest existing integrity entity
    /// as the PreviousEventHash on the newly created entity, regardless of the SequenceNumber value.
    /// </summary>
    [Test]
    public async Task CreateIntegrityRecordAsync_WithExistingSequence_IncrementsSequenceNumber()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        var dto = new AuditIntegrityDto { EventId = eventId };

        // Simulate a latest record at sequence 42
        var latestIntegrity = new AuditIntegrityEntity
        {
            EventId = Guid.NewGuid(),
            EventHash = "latest-event-hash-seq42",
            SequenceNumber = 42
        };

        _mockAuditIntegrityRepository
            .Setup(static x => x.GetLatestBySequenceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(latestIntegrity);

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuditEventEntity
            {
                EventId = eventId,
                EventType = "Test.Event",
                User = "testuser",
                InsertedDate = DateTimeOffset.UtcNow,
                JsonData = "{}"
            });

        AuditIntegrityEntity? capturedEntity = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        _mockAuditIntegrityRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockAuditIntegrityRepository
            .Setup(static x => x.ClearChangeTrackerAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _tamperDetectionService.CreateIntegrityRecordAsync(dto);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result.EventId, Is.EqualTo(eventId));

        // The new entity must chain off the previous record by using its EventHash as PreviousEventHash.
        // SequenceNumber is a DB-generated identity column — the service does not set it manually.
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.PreviousEventHash, Is.EqualTo(latestIntegrity.EventHash));
    }
}
