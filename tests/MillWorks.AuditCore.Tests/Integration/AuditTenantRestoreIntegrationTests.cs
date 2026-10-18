using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>Real relational rollback after earlier restore batches have already been saved.</summary>
[TestFixture]
public sealed class AuditTenantRestoreIntegrationTests : SqliteIntegrationFixture
{
    [TestCase("foreign")]
    [TestCase("bad-hash")]
    [TestCase("valid")]
    public async Task TenantArchiveRestoresAllBatchesOrRollsBackForTheExpectedReason(string scenario)
    {
        using var db = CreateContext();
        bool badHash = scenario == "bad-hash";
        bool foreign = scenario == "foreign";
        var tenant = Guid.NewGuid();
        var events = Enumerable.Range(0, 501).Select(i => new AuditArchivedEvent
        {
            EventId = Guid.NewGuid(), TenantId = foreign && i == 500 ? Guid.NewGuid() : tenant,
            UserId = Guid.NewGuid(), EventType = "owned", JsonData = new string('x', 200)
        }).ToArray();
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            await using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                await JsonSerializer.SerializeAsync(gzip, new { events });
            bytes = buffer.ToArray();
        }
        db.ArchiveRecords.Add(new AuditArchiveRecordEntity
        {
            ArchiveId = "owned", BlobName = "owned.gz", ContainerName = "fixture",
            Status = MillWorksArchiveStatus.Completed,
            Metadata = JsonSerializer.Serialize(new { TenantId = tenant }),
            Hash = badHash ? "invalid-hash" : Convert.ToBase64String(SHA256.HashData(bytes))
        });
        await db.SaveChangesAsync();
        var blobs = new Mock<BlobServiceClient>(MockBehavior.Strict);
        var container = new Mock<BlobContainerClient>(MockBehavior.Strict);
        var blob = new Mock<BlobClient>(MockBehavior.Strict);
        blobs.Setup(b => b.GetBlobContainerClient("fixture")).Returns(container.Object);
        container.Setup(c => c.GetBlobClient("owned.gz")).Returns(blob.Object);
        blob.Setup(b => b.ExistsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Response.FromValue(true, null!));
        blob.Setup(b => b.OpenReadAsync(It.IsAny<BlobOpenReadOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MemoryStream(bytes));
        var service = new AuditArchivalService(new AuditEventRepository(db), new AuditIntegrityRepository(db),
            new ArchiveRecordRepository(db), NullLogger<AuditArchivalService>.Instance,
            new ConfigurationBuilder().Build(), blobServiceClient: blobs.Object);
        var result = await service.RestoreTenantEventsAsync(tenant, "owned");
        Assert.That(result.Success, Is.EqualTo(scenario == "valid"));
        if (foreign) Assert.That(result.Message, Does.Contain("Archive content does not match the authorized tenant"));
        if (badHash) Assert.That(result.Message, Is.EqualTo("Archive integrity check failed"));
        db.ChangeTracker.Clear();
        Assert.That(await db.AuditEvents.CountAsync(), Is.EqualTo(scenario == "valid" ? 501 : 0));
        Assert.That(await db.AuditIntegrity.CountAsync(), Is.Zero);
    }
}
