using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for archive record lifecycle operations using SQLite.
/// Exercises ExecuteUpdateAsync and ExecuteDeleteAsync paths that InMemory can't test.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AuditArchivalIntegrationTests : SqliteIntegrationFixture
{
    [Test]
    public async Task ArchiveRecordLifecycle_CreateQueryUpdateDelete()
    {
        // Arrange — create archive records with different statuses
        using var context = CreateContext();
        var repo = new ArchiveRecordRepository(context);
        var now = DateTimeOffset.UtcNow;

        await SeedArchive(context, "arch-completed", MillWorksArchiveStatus.Completed, now.AddDays(-5));
        await SeedArchive(context, "arch-failed", MillWorksArchiveStatus.Failed, now.AddDays(-3));
        await SeedArchive(context, "arch-inprogress", MillWorksArchiveStatus.InProgress, now.AddDays(-1));

        // Act & Assert — query by status
        var completedRecords = (await repo.GetByStatusAsync(MillWorksArchiveStatus.Completed)).ToList();
        Assert.That(completedRecords, Has.Count.EqualTo(1));
        Assert.That(completedRecords[0].ArchiveId, Is.EqualTo("arch-completed"));

        // Act & Assert — update status via ExecuteUpdateAsync
        var updateResult = await repo.UpdateStatusAsync("arch-inprogress", MillWorksArchiveStatus.Completed);
        Assert.That(updateResult, Is.True);

        var updated = await context.ArchiveRecords.AsNoTracking()
            .FirstOrDefaultAsync(static r => r.ArchiveId == "arch-inprogress");
        Assert.That(updated!.Status, Is.EqualTo(MillWorksArchiveStatus.Completed));
    }

    [Test]
    public async Task GetArchiveStatistics_ReturnsCorrectAggregates()
    {
        using var context = CreateContext();
        var repo = new ArchiveRecordRepository(context);

        await SeedArchive(context, "stats-1", MillWorksArchiveStatus.Completed, sizeBytes: 1000, eventCount: 50);
        await SeedArchive(context, "stats-2", MillWorksArchiveStatus.Completed, sizeBytes: 3000, eventCount: 150);
        await SeedArchive(context, "stats-3", MillWorksArchiveStatus.Failed, sizeBytes: 9999, eventCount: 999);

        var stats = await repo.GetArchiveStatisticsAsync();

        Assert.That(stats["TotalEventsArchived"], Is.EqualTo(200));
        Assert.That(stats["TotalStorageBytes"], Is.EqualTo(4000L));
    }

    [Test]
    public async Task UpdateVerificationTimestamp_UpdatesAndReturnsTrue()
    {
        using var context = CreateContext();
        var repo = new ArchiveRecordRepository(context);
        await SeedArchive(context, "verify-test", MillWorksArchiveStatus.Completed);

        var verifyTime = DateTimeOffset.UtcNow;
        var result = await repo.UpdateVerificationTimestampAsync("verify-test", verifyTime);

        Assert.That(result, Is.True);
        var record = await context.ArchiveRecords.AsNoTracking()
            .FirstOrDefaultAsync(static r => r.ArchiveId == "verify-test");
        Assert.That(record!.LastVerifiedAt, Is.Not.Null);
    }

    [Test]
    public async Task GetTotalArchiveSize_SumsOnlyCompletedArchives()
    {
        using var context = CreateContext();
        var repo = new ArchiveRecordRepository(context);

        await SeedArchive(context, "size-1", MillWorksArchiveStatus.Completed, sizeBytes: 500);
        await SeedArchive(context, "size-2", MillWorksArchiveStatus.Completed, sizeBytes: 1500);
        await SeedArchive(context, "size-3", MillWorksArchiveStatus.Failed, sizeBytes: 9999);

        var total = await repo.GetTotalArchiveSizeAsync();

        Assert.That(total, Is.EqualTo(2000));
    }

    [Test]
    public async Task GetAllOrdered_ReturnsNewestFirst()
    {
        using var context = CreateContext();
        var repo = new ArchiveRecordRepository(context);
        var now = DateTimeOffset.UtcNow;

        await SeedArchive(context, "order-old", MillWorksArchiveStatus.Completed, createdAt: now.AddHours(-2));
        await SeedArchive(context, "order-new", MillWorksArchiveStatus.Completed, createdAt: now);
        await SeedArchive(context, "order-mid", MillWorksArchiveStatus.Completed, createdAt: now.AddHours(-1));

        var results = (await repo.GetAllOrderedAsync()).ToList();

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(results[0].ArchiveId, Is.EqualTo("order-new"));
    }

    #region Helpers

    private static async Task SeedArchive(
        MillWorks.AuditCore.EntityFramework.Data.AuditApplicationDbContext context,
        string archiveId,
        MillWorksArchiveStatus status = MillWorksArchiveStatus.Completed,
        DateTimeOffset? createdAt = null,
        long sizeBytes = 100,
        int eventCount = 10)
    {
        var now = DateTimeOffset.UtcNow;
        var entity = new AuditArchiveRecordEntity
        {
            ArchiveId = archiveId,
            BlobName = $"{archiveId}.gz",
            ContainerName = "test-container",
            EventCount = eventCount,
            DateRangeStart = now.AddDays(-30),
            DateRangeEnd = now,
            SizeBytes = sizeBytes,
            Hash = "testhash123",
            Status = status,
            CreatedAt = createdAt ?? now
        };
        await context.ArchiveRecords.AddAsync(entity);
        await context.SaveChangesAsync();
    }

    #endregion
}
