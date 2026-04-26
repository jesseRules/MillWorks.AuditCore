using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for AuditIntegrityRepository query and validation methods.
/// </summary>
[TestFixture]
public class AuditIntegrityRepositoryTests
{
    private DbContextOptions<AuditDbContext> _options;
    private AuditDbContext _context;
    private AuditIntegrityRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(_options);
        _repository = new AuditIntegrityRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region GetByEventIdAsync

    /// <summary>
    /// Verifies retrieval of integrity record by event ID.
    /// </summary>
    [Test]
    public async Task GetByEventIdAsync_WithExistingEvent_ReturnsRecord()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        await SeedIntegrity(eventId, 1, "hash1", null);

        // Act
        var result = await _repository.GetByEventIdAsync(eventId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.EventId, Is.EqualTo(eventId));
    }

    /// <summary>
    /// Verifies null return for non-existent event ID.
    /// </summary>
    [Test]
    public async Task GetByEventIdAsync_WithNonExistentEvent_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByEventIdAsync(Guid.NewGuid());

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region GetLatestBySequenceAsync

    /// <summary>
    /// Verifies the latest record by sequence number is returned.
    /// </summary>
    [Test]
    public async Task GetLatestBySequenceAsync_ReturnsHighestSequence()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null);
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2");
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1");

        // Act
        var latest = await _repository.GetLatestBySequenceAsync();

        // Assert
        Assert.That(latest, Is.Not.Null);
        Assert.That(latest!.SequenceNumber, Is.EqualTo(3));
    }

    /// <summary>
    /// Verifies null is returned when no records exist.
    /// </summary>
    [Test]
    public async Task GetLatestBySequenceAsync_EmptyTable_ReturnsNull()
    {
        // Act
        var latest = await _repository.GetLatestBySequenceAsync();

        // Assert
        Assert.That(latest, Is.Null);
    }

    #endregion

    #region GetBySequenceRangeAsync

    /// <summary>
    /// Verifies sequence range query returns records in order.
    /// </summary>
    [Test]
    public async Task GetBySequenceRangeAsync_ReturnsOrderedRecordsInRange()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null);
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1");
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2");
        await SeedIntegrity(Guid.NewGuid(), 4, "h4", "h3");

        // Act
        var results = (await _repository.GetBySequenceRangeAsync(2, 3)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results[0].SequenceNumber, Is.EqualTo(2));
        Assert.That(results[1].SequenceNumber, Is.EqualTo(3));
    }

    #endregion

    #region ValidateIntegrityChainAsync

    /// <summary>
    /// Verifies a valid chain returns true.
    /// </summary>
    [Test]
    public async Task ValidateIntegrityChainAsync_ValidChain_ReturnsTrue()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "hash1", null);
        await SeedIntegrity(Guid.NewGuid(), 2, "hash2", "hash1");
        await SeedIntegrity(Guid.NewGuid(), 3, "hash3", "hash2");

        // Act
        var isValid = await _repository.ValidateIntegrityChainAsync(1, 3);

        // Assert
        Assert.That(isValid, Is.True);
    }

    /// <summary>
    /// Verifies a broken hash chain returns false.
    /// </summary>
    [Test]
    public async Task ValidateIntegrityChainAsync_BrokenHash_ReturnsFalse()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "hash1", null);
        await SeedIntegrity(Guid.NewGuid(), 2, "hash2", "WRONG_HASH");

        // Act
        var isValid = await _repository.ValidateIntegrityChainAsync(1, 2);

        // Assert
        Assert.That(isValid, Is.False);
    }

    /// <summary>
    /// Verifies a gap in sequence numbers returns false.
    /// </summary>
    [Test]
    public async Task ValidateIntegrityChainAsync_SequenceGap_ReturnsFalse()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "hash1", null);
        await SeedIntegrity(Guid.NewGuid(), 3, "hash3", "hash1"); // gap: seq 2 missing

        // Act
        var isValid = await _repository.ValidateIntegrityChainAsync(1, 3);

        // Assert
        Assert.That(isValid, Is.False);
    }

    /// <summary>
    /// Verifies an empty range is considered valid.
    /// </summary>
    [Test]
    public async Task ValidateIntegrityChainAsync_EmptyRange_ReturnsTrue()
    {
        // Act
        var isValid = await _repository.ValidateIntegrityChainAsync(100, 200);

        // Assert
        Assert.That(isValid, Is.True);
    }

    #endregion

    #region GetByAlgorithmVersionAsync

    /// <summary>
    /// Verifies filtering by algorithm version.
    /// </summary>
    [Test]
    public async Task GetByAlgorithmVersionAsync_ReturnsMatchingRecords()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null, algorithmVersion: 1);
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1", algorithmVersion: 2);
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2", algorithmVersion: 1);

        // Act
        var results = (await _repository.GetByAlgorithmVersionAsync(1)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static r => r.AlgorithmVersion == 1), Is.True);
    }

    #endregion

    #region GetAllSequenceNumbersAsync

    /// <summary>
    /// Verifies all sequence numbers returned in order.
    /// </summary>
    [Test]
    public async Task GetAllSequenceNumbersAsync_ReturnsOrderedSequenceNumbers()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2");
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null);
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1");

        // Act
        var sequences = (await _repository.GetAllSequenceNumbersAsync()).ToList();

        // Assert
        Assert.That(sequences, Has.Count.EqualTo(3));
        Assert.That(sequences, Is.EqualTo(new List<long> { 1, 2, 3 }));
    }

    #endregion

    #region GetByTrustedTimestampRangeAsync

    /// <summary>
    /// Verifies records within the trusted timestamp range are returned.
    /// </summary>
    [Test]
    public async Task GetByTrustedTimestampRangeAsync_ReturnsRecordsInRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null, trustedTimestamp: now.AddDays(-10));
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1", trustedTimestamp: now.AddDays(-3));
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2", trustedTimestamp: now);

        // Act
        var results = (await _repository.GetByTrustedTimestampRangeAsync(now.AddDays(-5), now.AddDays(-1))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].SequenceNumber, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies empty result when no records are in range.
    /// </summary>
    [Test]
    public async Task GetByTrustedTimestampRangeAsync_NoRecordsInRange_ReturnsEmpty()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null, trustedTimestamp: now.AddDays(-30));

        // Act
        var results = (await _repository.GetByTrustedTimestampRangeAsync(now.AddDays(-5), now)).ToList();

        // Assert
        Assert.That(results, Is.Empty);
    }

    #endregion

    #region GetWithAuditEventsAsync

    /// <summary>
    /// Verifies records are returned with included audit events.
    /// </summary>
    [Test]
    public async Task GetWithAuditEventsAsync_IncludesRelatedEvents()
    {
        // Arrange
        var eventId = Guid.NewGuid();
        await SeedIntegrity(eventId, 1, "h1", null);

        // Act
        var results = (await _repository.GetWithAuditEventsAsync()).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].AuditEvent, Is.Not.Null);
        Assert.That(results[0].AuditEvent!.EventId, Is.EqualTo(eventId));
    }

    /// <summary>
    /// Verifies date filters are respected.
    /// </summary>
    [Test]
    public async Task GetWithAuditEventsAsync_WithDateFilters_RespectsRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null, trustedTimestamp: now.AddDays(-10));
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1", trustedTimestamp: now.AddDays(-3));
        await SeedIntegrity(Guid.NewGuid(), 3, "h3", "h2", trustedTimestamp: now);

        // Act
        var results = (await _repository.GetWithAuditEventsAsync(now.AddDays(-5), now.AddDays(-1))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].SequenceNumber, Is.EqualTo(2));
    }

    /// <summary>
    /// Verifies no date filters returns all records.
    /// </summary>
    [Test]
    public async Task GetWithAuditEventsAsync_NoDateFilters_ReturnsAll()
    {
        // Arrange
        await SeedIntegrity(Guid.NewGuid(), 1, "h1", null);
        await SeedIntegrity(Guid.NewGuid(), 2, "h2", "h1");

        // Act
        var results = (await _repository.GetWithAuditEventsAsync()).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
    }

    #endregion

    #region Helpers

    private async Task SeedIntegrity(Guid eventId, long sequenceNumber, string eventHash, string? previousHash,
        int algorithmVersion = 1, DateTimeOffset? trustedTimestamp = null)
    {
        // First seed an AuditEvent entity (FK requirement)
        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test",
            InsertedDate = DateTimeOffset.UtcNow
        };
        await _context.AuditEvents.AddAsync(auditEvent);

        var entity = new AuditIntegrityEntity
        {
            EventId = eventId,
            SequenceNumber = sequenceNumber,
            EventHash = eventHash,
            PreviousEventHash = previousHash,
            TrustedTimestamp = trustedTimestamp ?? DateTimeOffset.UtcNow,
            Checksum = "checksum",
            AlgorithmVersion = algorithmVersion
        };
        await _context.AuditIntegrity.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    #endregion
}
