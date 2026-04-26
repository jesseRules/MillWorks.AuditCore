using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Tests.Helpers;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Tests for ArchiveRecordRepository CRUD, statistics, and cleanup methods.
/// </summary>
[TestFixture]
public class ArchiveRecordRepositoryTests
{
    private DbContextOptions<AuditDbContext> _options;
    private AuditDbContext _context;
    private ArchiveRecordRepository _repository;

    [SetUp]
    public void Setup()
    {
        _options = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditDbContext(_options);
        _repository = new ArchiveRecordRepository(_context);
    }

    [TearDown]
    public void TearDown()
    {
        _repository.Dispose();
        _context.Dispose();
    }

    #region GetByArchiveIdAsync

    /// <summary>
    /// Verifies retrieval by archive ID.
    /// </summary>
    [Test]
    public async Task GetByArchiveIdAsync_ReturnsMatchingRecord()
    {
        // Arrange
        var archiveId = $"archive-{Guid.NewGuid():N}";
        await SeedArchive(archiveId);

        // Act
        var result = await _repository.GetByArchiveIdAsync(archiveId);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.ArchiveId, Is.EqualTo(archiveId));
    }

    /// <summary>
    /// Verifies null for non-existent archive ID.
    /// </summary>
    [Test]
    public async Task GetByArchiveIdAsync_NonExistent_ReturnsNull()
    {
        // Act
        var result = await _repository.GetByArchiveIdAsync("non-existent");

        // Assert
        Assert.That(result, Is.Null);
    }

    #endregion

    #region GetAllOrderedAsync

    /// <summary>
    /// Verifies all records are returned ordered by CreatedAt descending.
    /// </summary>
    [Test]
    public async Task GetAllOrderedAsync_ReturnsOrderedByCreatedAtDesc()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedArchive("a1", createdAt: now.AddHours(-2));
        await SeedArchive("a2", createdAt: now);
        await SeedArchive("a3", createdAt: now.AddHours(-1));

        // Act
        var results = (await _repository.GetAllOrderedAsync()).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results[0].ArchiveId, Is.EqualTo("a2"));
    }

    #endregion

    #region GetByStatusAsync

    /// <summary>
    /// Verifies filtering by status.
    /// </summary>
    [Test]
    public async Task GetByStatusAsync_ReturnsMatchingRecords()
    {
        // Arrange
        await SeedArchive("completed1", status: MillWorksArchiveStatus.Completed);
        await SeedArchive("failed1", status: MillWorksArchiveStatus.Failed);
        await SeedArchive("completed2", status: MillWorksArchiveStatus.Completed);

        // Act
        var results = (await _repository.GetByStatusAsync(MillWorksArchiveStatus.Completed)).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
        Assert.That(results.All(static r => r.Status == MillWorksArchiveStatus.Completed), Is.True);
    }

    #endregion

    #region GetByDateRangeAsync

    /// <summary>
    /// Verifies date range filtering by CreatedAt.
    /// </summary>
    [Test]
    public async Task GetByDateRangeAsync_ReturnsRecordsInRange()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        await SeedArchive("old", createdAt: now.AddDays(-10));
        await SeedArchive("mid", createdAt: now.AddDays(-3));
        await SeedArchive("new", createdAt: now);

        // Act
        var results = (await _repository.GetByDateRangeAsync(now.AddDays(-5), now.AddDays(-1))).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(1));
        Assert.That(results[0].ArchiveId, Is.EqualTo("mid"));
    }

    #endregion

    #region GetByContainerNameAsync

    /// <summary>
    /// Verifies filtering by container name.
    /// </summary>
    [Test]
    public async Task GetByContainerNameAsync_ReturnsMatchingRecords()
    {
        // Arrange
        await SeedArchive("a1", containerName: "audit-archives");
        await SeedArchive("a2", containerName: "backup-archives");
        await SeedArchive("a3", containerName: "audit-archives");

        // Act
        var results = (await _repository.GetByContainerNameAsync("audit-archives")).ToList();

        // Assert
        Assert.That(results, Has.Count.EqualTo(2));
    }

    #endregion

    #region GetTotalArchiveSizeAsync

    /// <summary>
    /// Verifies total size sums only completed archives.
    /// </summary>
    [Test]
    public async Task GetTotalArchiveSizeAsync_SumsCompletedOnly()
    {
        // Arrange
        await SeedArchive("a1", status: MillWorksArchiveStatus.Completed, sizeBytes: 1000);
        await SeedArchive("a2", status: MillWorksArchiveStatus.Completed, sizeBytes: 2000);
        await SeedArchive("a3", status: MillWorksArchiveStatus.Failed, sizeBytes: 9999);

        // Act
        var total = await _repository.GetTotalArchiveSizeAsync();

        // Assert
        Assert.That(total, Is.EqualTo(3000));
    }

    #endregion

    #region GetArchiveStatisticsAsync

    /// <summary>
    /// Verifies that statistics are computed for completed archives.
    /// </summary>
    [Test]
    public async Task GetArchiveStatisticsAsync_ReturnsExpectedKeys()
    {
        // Arrange
        await SeedArchive("a1", status: MillWorksArchiveStatus.Completed, sizeBytes: 1000, eventCount: 50);
        await SeedArchive("a2", status: MillWorksArchiveStatus.Completed, sizeBytes: 3000, eventCount: 150);

        // Act
        var stats = await _repository.GetArchiveStatisticsAsync();

        // Assert
        Assert.That(stats, Does.ContainKey("TotalEventsArchived"));
        Assert.That(stats, Does.ContainKey("TotalStorageBytes"));
        Assert.That(stats["TotalEventsArchived"], Is.EqualTo(200));
        Assert.That(stats["TotalStorageBytes"], Is.EqualTo(4000L));
    }

    #endregion

    #region GetPagedAsync (archive-specific)

    /// <summary>
    /// Verifies paging with status filter.
    /// </summary>
    [Test]
    public async Task GetPagedAsync_WithStatusFilter_ReturnsFilteredPage()
    {
        // Arrange
        for (int i = 0; i < 15; i++)
            await SeedArchive($"completed-{i}", status: MillWorksArchiveStatus.Completed);
        for (int i = 0; i < 5; i++)
            await SeedArchive($"failed-{i}", status: MillWorksArchiveStatus.Failed);

        // Act
        var (records, totalCount) = await _repository.GetPagedAsync(1, 10, MillWorksArchiveStatus.Completed);
        var recordList = records.ToList();

        // Assert
        Assert.That(totalCount, Is.EqualTo(15));
        Assert.That(recordList, Has.Count.EqualTo(10));
    }

    #endregion

    #region GetByEventDateRangeAsync

    /// <summary>
    /// Verifies archives whose event date range overlaps the query range are returned.
    /// </summary>
    [Test]
    public async Task GetByEventDateRangeAsync_OverlappingRanges_ReturnsMatches()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        // Archive fully inside query range
        await SeedArchive("inside", dateRangeStart: now.AddDays(-5), dateRangeEnd: now.AddDays(-3));
        // Archive partially overlapping (start before, end inside)
        await SeedArchive("overlap-start", dateRangeStart: now.AddDays(-10), dateRangeEnd: now.AddDays(-4));
        // Archive completely outside query range
        await SeedArchive("outside", dateRangeStart: now.AddDays(-20), dateRangeEnd: now.AddDays(-15));
        // Archive that wraps the query range
        await SeedArchive("wrapper", dateRangeStart: now.AddDays(-8), dateRangeEnd: now.AddDays(-1));

        // Act — query range: -7 to -2
        var results = (await _repository.GetByEventDateRangeAsync(now.AddDays(-7), now.AddDays(-2))).ToList();

        // Assert
        Assert.That(results.Select(static r => r.ArchiveId), Does.Contain("inside"));
        Assert.That(results.Select(static r => r.ArchiveId), Does.Contain("overlap-start"));
        Assert.That(results.Select(static r => r.ArchiveId), Does.Contain("wrapper"));
        Assert.That(results.Select(static r => r.ArchiveId), Does.Not.Contain("outside"));
    }

    #endregion

    #region GetArchivesNeedingVerificationAsync

    /// <summary>
    /// Verifies archives with stale verification are returned, recent are excluded.
    /// </summary>
    [Test]
    public async Task GetArchivesNeedingVerificationAsync_ReturnsStaleExcludesRecent()
    {
        // Arrange
        var now = DateTimeOffset.UtcNow;
        // Never verified
        await SeedArchive("never-verified", lastVerifiedAt: null);
        // Verified 48 hours ago (stale with default 24h interval)
        await SeedArchive("stale", lastVerifiedAt: now.AddHours(-48));
        // Verified 1 hour ago (recent)
        await SeedArchive("recent", lastVerifiedAt: now.AddHours(-1));
        // Failed status (should not be returned)
        await SeedArchive("failed-status", status: MillWorksArchiveStatus.Failed, lastVerifiedAt: null);

        // Act
        var results = (await _repository.GetArchivesNeedingVerificationAsync(24)).ToList();

        // Assert
        var archiveIds = results.Select(static r => r.ArchiveId).ToList();
        Assert.That(archiveIds, Does.Contain("never-verified"));
        Assert.That(archiveIds, Does.Contain("stale"));
        Assert.That(archiveIds, Does.Not.Contain("recent"));
        Assert.That(archiveIds, Does.Not.Contain("failed-status"));
    }

    #endregion

    // ExecuteUpdate/ExecuteDelete tests moved to Integration/ArchiveRecordIntegrationTests.cs

    #region Helpers

    private async Task SeedArchive(
        string archiveId,
        MillWorksArchiveStatus status = MillWorksArchiveStatus.Completed,
        long sizeBytes = 100,
        int eventCount = 10,
        string containerName = "default-container",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? dateRangeStart = null,
        DateTimeOffset? dateRangeEnd = null,
        DateTimeOffset? lastVerifiedAt = null)
    {
        var entity = new AuditArchiveRecordEntity
        {
            ArchiveId = archiveId,
            BlobName = $"{archiveId}.gz",
            ContainerName = containerName,
            EventCount = eventCount,
            DateRangeStart = dateRangeStart ?? DateTimeOffset.UtcNow.AddDays(-30),
            DateRangeEnd = dateRangeEnd ?? DateTimeOffset.UtcNow,
            SizeBytes = sizeBytes,
            Hash = "abc123",
            Status = status,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastVerifiedAt = lastVerifiedAt
        };
        await _context.ArchiveRecords.AddAsync(entity);
        await _context.SaveChangesAsync();
    }

    #endregion
}
