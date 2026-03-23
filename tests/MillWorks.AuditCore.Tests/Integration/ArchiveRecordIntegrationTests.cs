using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for ArchiveRecordRepository methods that require a relational database
/// (ExecuteUpdateAsync, ExecuteDeleteAsync).
/// </summary>
[TestFixture]
[Category("Integration")]
public class ArchiveRecordIntegrationTests : SqliteIntegrationFixture
{
    #region UpdateVerificationTimestampAsync

    [Test]
    public async Task UpdateVerificationTimestampAsync_MissingArchive_ReturnsFalse()
    {
        using var context = CreateContext();
        var repository = new ArchiveRecordRepository(context);

        var result = await repository.UpdateVerificationTimestampAsync("non-existent", DateTimeOffset.UtcNow);

        Assert.That(result, Is.False);
    }

    #endregion

    #region UpdateStatusAsync

    [Test]
    public async Task UpdateStatusAsync_UpdatesStatusAndErrorMessage()
    {
        using var context = CreateContext();
        var repository = new ArchiveRecordRepository(context);
        await SeedArchive(context, "status-test", status: MillWorksArchiveStatus.InProgress);

        var result = await repository.UpdateStatusAsync("status-test", MillWorksArchiveStatus.Failed, "Something went wrong");

        Assert.That(result, Is.True);
        var updated = await context.ArchiveRecords.AsNoTracking()
            .FirstOrDefaultAsync(static ar => ar.ArchiveId == "status-test");
        Assert.That(updated, Is.Not.Null);
        Assert.That(updated!.Status, Is.EqualTo(MillWorksArchiveStatus.Failed));
        Assert.That(updated.ErrorMessage, Is.EqualTo("Something went wrong"));
    }

    [Test]
    public async Task UpdateStatusAsync_MissingArchive_ReturnsFalse()
    {
        using var context = CreateContext();
        var repository = new ArchiveRecordRepository(context);

        var result = await repository.UpdateStatusAsync("non-existent", MillWorksArchiveStatus.Failed);

        Assert.That(result, Is.False);
    }

    #endregion

    #region CleanupOldArchiveRecordsAsync

    [Test]
    public async Task CleanupOldArchiveRecordsAsync_DeletesOldCompletedAndFailed_KeepsInProgress()
    {
        using var context = CreateContext();
        var repository = new ArchiveRecordRepository(context);
        var now = DateTimeOffset.UtcNow;

        await SeedArchive(context, "old-completed", status: MillWorksArchiveStatus.Completed, createdAt: now.AddDays(-60));
        await SeedArchive(context, "old-failed", status: MillWorksArchiveStatus.Failed, createdAt: now.AddDays(-60));
        await SeedArchive(context, "old-inprogress", status: MillWorksArchiveStatus.InProgress, createdAt: now.AddDays(-60));
        await SeedArchive(context, "recent-completed", status: MillWorksArchiveStatus.Completed, createdAt: now.AddDays(-5));

        var deletedCount = await repository.CleanupOldArchiveRecordsAsync(30);

        Assert.That(deletedCount, Is.EqualTo(2));
        var remaining = await context.ArchiveRecords.Select(static r => r.ArchiveId).ToListAsync();
        Assert.That(remaining, Does.Contain("old-inprogress"));
        Assert.That(remaining, Does.Contain("recent-completed"));
        Assert.That(remaining, Does.Not.Contain("old-completed"));
        Assert.That(remaining, Does.Not.Contain("old-failed"));
    }

    #endregion

    #region Helpers

    private static async Task SeedArchive(
        MillWorks.AuditCore.EntityFramework.Data.AuditApplicationDbContext context,
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
        await context.ArchiveRecords.AddAsync(entity);
        await context.SaveChangesAsync();
    }

    #endregion
}
