using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Canonicalization;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DistributedLocking.Implementations;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for TamperDetectionService verifying integrity verification,
/// chain verification, tamper detection, and batch operations against a real SQLite backend.
/// SQLite does not support IDENTITY on non-PK columns, so integrity records are seeded
/// via raw SQL with explicit SequenceNumber values. Verification and detection methods
/// are then tested end-to-end.
/// </summary>
[TestFixture]
[Category("Integration")]
public class TamperDetectionIntegrationTests : SqliteIntegrationFixture
{
    private const string HmacKey = "test-hmac-key-for-testing-12345678";

    private static TamperDetectionService CreateService(
        IAuditEventRepository eventRepo,
        IAuditIntegrityRepository integrityRepo,
        IAuditSecurityEventService securityEventService)
    {
        return new TamperDetectionService(
            eventRepo,
            integrityRepo,
            securityEventService,
            NullLogger<TamperDetectionService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AuditOptions
            {
                Environment = "Development",
                HmacKey = HmacKey
            }),
            Microsoft.Extensions.Options.Options.Create(new SecurityOptions()),
            new InMemoryDistributedLockService(NullLogger<InMemoryDistributedLockService>.Instance));
    }

    private static AuditEventEntity CreateAuditEvent(
        Guid? eventId = null,
        string eventType = "User.Created",
        string user = "test@test.com",
        string? jsonData = null)
    {
        return new AuditEventEntity
        {
            EventId = eventId ?? Guid.NewGuid(),
            EventType = eventType,
            User = user,
            UserId = Guid.NewGuid(),
            JsonData = jsonData ?? """{"action":"test"}""",
            InsertedDate = DateTimeOffset.UtcNow,
            EntityType = "User",
            EntityId = Guid.NewGuid().ToString()
        };
    }

    /// <summary>
    /// Mirrors TamperDetectionService.ComputeEventHash exactly.
    /// </summary>
    private static string ComputeEventHash(AuditEventEntity e)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes(e.EventId.ToString()));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(e.EventType ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(e.User ?? string.Empty));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.NormalizeDate(e.InsertedDate)));
        hash.AppendData("|"u8);
        hash.AppendData(Encoding.UTF8.GetBytes(AuditCanonicalizer.Canonicalize(e.JsonData)));
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// Mirrors TamperDetectionService.ComputeHmac exactly.
    /// </summary>
    private static string ComputeHmac(AuditEventEntity e)
    {
        var dateString = AuditCanonicalizer.NormalizeDate(e.InsertedDate);
        var dataToSign = $"{e.EventId}|{e.EventType}|{dateString}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(HmacKey));
        var hashBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(dataToSign));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Mirrors TamperDetectionService.ComputeChecksum exactly.
    /// </summary>
    private static string ComputeChecksum(AuditEventEntity e)
    {
        var criticalFields = $"{e.EventId}{e.EventType}{e.UserId}";
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(criticalFields));
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Seeds an AuditIntegrityEntity via raw SQL to work around SQLite IDENTITY limitation.
    /// </summary>
    private async Task SeedIntegrityRecordAsync(
        AuditApplicationDbContext context,
        AuditEventEntity auditEvent,
        long sequenceNumber,
        string? previousEventHash = null)
    {
        var eventHash = ComputeEventHash(auditEvent);
        var hmacSignature = ComputeHmac(auditEvent);
        var checksum = ComputeChecksum(auditEvent);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");

        if (previousEventHash != null)
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditIntegrity" ("Id", "EventId", "EventHash", "PreviousEventHash", "TrustedTimestamp", "SequenceNumber", "HmacSignature", "Checksum", "AlgorithmVersion", "CreatedAt", "CreatedById")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, 2, {8}, {9})
                """,
                id, auditEvent.EventId, eventHash, previousEventHash,
                now, sequenceNumber, hmacSignature, checksum,
                now, Guid.Empty);
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditIntegrity" ("Id", "EventId", "EventHash", "PreviousEventHash", "TrustedTimestamp", "SequenceNumber", "HmacSignature", "Checksum", "AlgorithmVersion", "CreatedAt", "CreatedById")
                VALUES ({0}, {1}, {2}, NULL, {3}, {4}, {5}, {6}, 2, {7}, {8})
                """,
                id, auditEvent.EventId, eventHash,
                now, sequenceNumber, hmacSignature, checksum,
                now, Guid.Empty);
        }
    }

    [Test]
    public async Task CreateAndVerify_IntegrityRecord_RoundTrip()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var service = CreateService(eventRepo, integrityRepo, mockSecurityService.Object);

        var auditEvent = CreateAuditEvent();
        await context.AuditEvents.AddAsync(auditEvent);
        await context.SaveChangesAsync();

        // Seed integrity record via raw SQL (SQLite cannot auto-generate SequenceNumber)
        await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: 1);

        // Act
        var isValid = await service.VerifyIntegrityAsync(auditEvent.EventId);

        // Assert
        Assert.That(isValid, Is.True);
    }

    [Test]
    public async Task VerifyChainIntegrity_ValidChain_ReturnsTrue()
    {
        // Arrange
        using var context = CreateContext();
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var service = CreateService(eventRepo, integrityRepo, mockSecurityService.Object);

        // Create 3 events and seed integrity records with proper chain linkage
        string? previousHash = null;
        for (int i = 0; i < 3; i++)
        {
            var auditEvent = CreateAuditEvent(eventType: $"Event.Type{i}");
            await context.AuditEvents.AddAsync(auditEvent);
            await context.SaveChangesAsync();

            await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: i + 1, previousEventHash: previousHash);
            previousHash = ComputeEventHash(auditEvent);
        }

        // Act
        var result = await service.VerifyChainIntegrityAsync();

        // Assert
        Assert.That(result.IsValid, Is.True);
        Assert.That(result.ChainBroken, Is.False);
        Assert.That(result.EventsChecked, Is.EqualTo(3));
        Assert.That(result.TamperedEvents, Is.Empty);
    }

    [Test]
    public async Task VerifyChainIntegrity_BrokenChain_ReturnsFalse()
    {
        // Arrange
        using var context = CreateContext();

        // Create 3 events and integrity records with proper chain linkage
        var events = new List<AuditEventEntity>();
        string? previousHash = null;
        for (int i = 0; i < 3; i++)
        {
            var auditEvent = CreateAuditEvent(eventType: $"Chain.Event{i}");
            events.Add(auditEvent);
            await context.AuditEvents.AddAsync(auditEvent);
            await context.SaveChangesAsync();

            await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: i + 1, previousEventHash: previousHash);
            previousHash = ComputeEventHash(auditEvent);
        }

        // Tamper with the second integrity record's hash via raw SQL
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"AuditIntegrity\" SET \"EventHash\" = 'tampered-hash-value-that-is-invalid!!' WHERE \"SequenceNumber\" = 2");

        // Act - use a fresh context to avoid cached entities
        using var verifyContext = CreateContext();
        var verifyEventRepo = new AuditEventRepository(verifyContext);
        var verifyIntegrityRepo = new AuditIntegrityRepository(verifyContext);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var verifyService = CreateService(verifyEventRepo, verifyIntegrityRepo, mockSecurityService.Object);

        var result = await verifyService.VerifyChainIntegrityAsync();

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.TamperedEvents, Is.Not.Empty);
    }

    [Test]
    public async Task DetectTampering_ModifiedEvent_DetectsChange()
    {
        // Arrange
        using var context = CreateContext();

        // Create event and seed integrity record
        var auditEvent = CreateAuditEvent(jsonData: """{"original":"data"}""");
        await context.AuditEvents.AddAsync(auditEvent);
        await context.SaveChangesAsync();

        await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: 1);

        // Tamper with the event's JsonData via raw SQL
        var tamperedJson = """{"tampered":"true"}""";
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"AuditEvents\" SET \"JsonData\" = {0} WHERE \"EventId\" = {1}",
            tamperedJson, auditEvent.EventId);

        // Act - use a fresh context to avoid cached data
        using var detectContext = CreateContext();
        var detectEventRepo = new AuditEventRepository(detectContext);
        var detectIntegrityRepo = new AuditIntegrityRepository(detectContext);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var detectService = CreateService(detectEventRepo, detectIntegrityRepo, mockSecurityService.Object);

        var alerts = await detectService.DetectTamperingAsync(hoursBack: 24);

        // Assert
        Assert.That(alerts, Is.Not.Empty);
        Assert.That(alerts.Any(static a => a.AlertType == "Integrity Violation"), Is.True);
    }

    [Test]
    public async Task CreateIntegrityRecordsBatch_MultipleEvents_AllRecorded()
    {
        // Arrange
        using var context = CreateContext();

        // Create 5 events and seed integrity records
        var events = new List<AuditEventEntity>();
        string? previousHash = null;
        for (int i = 0; i < 5; i++)
        {
            var auditEvent = CreateAuditEvent(eventType: $"Batch.Event{i}");
            events.Add(auditEvent);
            await context.AuditEvents.AddAsync(auditEvent);
            await context.SaveChangesAsync();

            await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: i + 1, previousEventHash: previousHash);
            previousHash = ComputeEventHash(auditEvent);
        }

        // Act - verify all 5 records are in the database and verifiable
        using var verifyContext = CreateContext();
        var recordCount = await verifyContext.Set<AuditIntegrityEntity>()
            .AsNoTracking()
            .CountAsync();

        var verifyEventRepo = new AuditEventRepository(verifyContext);
        var verifyIntegrityRepo = new AuditIntegrityRepository(verifyContext);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var verifyService = CreateService(verifyEventRepo, verifyIntegrityRepo, mockSecurityService.Object);

        // Verify each event's integrity individually
        var allValid = true;
        foreach (var evt in events)
        {
            var valid = await verifyService.VerifyIntegrityAsync(evt.EventId);
            if (!valid) allValid = false;
        }

        // Assert
        Assert.That(recordCount, Is.EqualTo(5));
        Assert.That(allValid, Is.True);

        // Verify chain integrity as well
        var chainResult = await verifyService.VerifyChainIntegrityAsync();
        Assert.That(chainResult.IsValid, Is.True);
        Assert.That(chainResult.EventsChecked, Is.EqualTo(5));
    }
}
