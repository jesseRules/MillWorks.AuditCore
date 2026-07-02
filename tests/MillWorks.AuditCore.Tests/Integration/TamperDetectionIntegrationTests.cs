using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Canonicalization;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Tests.Helpers;

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

    // The integrity HMAC now resolves through an ISigningKeyProvider keyed by id. The seed below and
    // the service signer must share the same raw key bytes and key id so seeded HMACs verify.
    private const string HmacKeyId = "test-hmac-key-v1";
    private static byte[] HmacKeyBytes => Encoding.UTF8.GetBytes(HmacKey);

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
            IntegrityTestCrypto.Hasher,
            IntegrityTestCrypto.CreateHmacSigner(HmacKeyBytes, HmacKeyId));
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
    /// Mirrors TamperDetectionService.ComputeHmac exactly (v3 format: includes chain metadata).
    /// </summary>
    private static string ComputeHmac(
        string eventHash,
        string? previousEventHash,
        long sequenceNumber,
        DateTimeOffset trustedTimestamp)
    {
        var timestampString = AuditCanonicalizer.NormalizeDate(trustedTimestamp);
        var previous = previousEventHash ?? string.Empty;

        using var hash = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(HmacKey));
        AppendLengthPrefixed(hash, eventHash);
        AppendLengthPrefixed(hash, previous);
        AppendLengthPrefixed(hash, sequenceNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendLengthPrefixed(hash, timestampString);
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// Mirrors TamperDetectionService.ComputeChecksum exactly (v3 format: length-prefixed).
    /// </summary>
    private static string ComputeChecksum(AuditEventEntity e)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendLengthPrefixed(hash, e.EventId.ToString());
        AppendLengthPrefixed(hash, e.EventType ?? string.Empty);
        AppendLengthPrefixed(hash, e.UserId?.ToString() ?? string.Empty);
        return Convert.ToBase64String(hash.GetHashAndReset());
    }

    /// <summary>
    /// Length-prefix helper matching TamperDetectionService.AppendLengthPrefixed.
    /// </summary>
    private static void AppendLengthPrefixed(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthBytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(lengthBytes, bytes.Length);
        hash.AppendData(lengthBytes);
        hash.AppendData(bytes);
    }

    /// <summary>
    /// Seeds an AuditIntegrityEntity via raw SQL to work around SQLite IDENTITY limitation.
    /// Uses v3 algorithm format (chain-aware HMAC).
    /// </summary>
    private async Task SeedIntegrityRecordAsync(
        AuditDbContext context,
        AuditEventEntity auditEvent,
        long sequenceNumber,
        string? previousEventHash = null)
    {
        var eventHash = ComputeEventHash(auditEvent);
        var checksum = ComputeChecksum(auditEvent);
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var now = timestamp.ToString("O");

        // v3 HMAC includes chain metadata
        var hmacSignature = ComputeHmac(eventHash, previousEventHash, sequenceNumber, timestamp);

        if (previousEventHash != null)
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditIntegrity" ("Id", "EventId", "EventHash", "PreviousEventHash", "TrustedTimestamp", "SequenceNumber", "HmacSignature", "HmacKeyId", "Checksum", "AlgorithmVersion", "CreatedAt", "CreatedById")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7}, {8}, 3, {9}, {10})
                """,
                id, auditEvent.EventId, eventHash, previousEventHash,
                now, sequenceNumber, hmacSignature, HmacKeyId, checksum,
                now, Guid.Empty);
        }
        else
        {
            await context.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO "AuditIntegrity" ("Id", "EventId", "EventHash", "PreviousEventHash", "TrustedTimestamp", "SequenceNumber", "HmacSignature", "HmacKeyId", "Checksum", "AlgorithmVersion", "CreatedAt", "CreatedById")
                VALUES ({0}, {1}, {2}, NULL, {3}, {4}, {5}, {6}, {7}, 3, {8}, {9})
                """,
                id, auditEvent.EventId, eventHash,
                now, sequenceNumber, hmacSignature, HmacKeyId, checksum,
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

    #region Finding #4: Missing audit event detection

    /// <summary>
    /// Verifies that when an integrity record exists but its associated audit event has been
    /// deleted, the chain verification detects this as tampering.
    /// Note: This is a unit test using mocks because EF Core's Include() behavior on SQLite
    /// filters out records where the related entity doesn't exist.
    /// </summary>
    [Test]
    public async Task VerifyChainIntegrity_MissingAuditEvent_DetectsAndReportsTamper()
    {
        // Arrange: Mock an integrity record with null AuditEvent
        var eventId = Guid.NewGuid();
        var integrityRecord = new AuditIntegrityEntity
        {
            EventId = eventId,
            EventHash = "hash",
            PreviousEventHash = null,
            SequenceNumber = 1,
            TrustedTimestamp = DateTimeOffset.UtcNow,
            HmacSignature = "hmac",
            Checksum = "checksum",
            AlgorithmVersion = AuditCanonicalizer.CurrentVersion,
            AuditEvent = null // Event was deleted!
        };

        var mockEventRepo = new Mock<IAuditEventRepository>();
        var mockIntegrityRepo = new Mock<IAuditIntegrityRepository>();
        var mockSecurityService = new Mock<IAuditSecurityEventService>();

        mockIntegrityRepo.SetupGet(static x => x.SupportsCrossProcessAppendLock).Returns(true);
        mockIntegrityRepo.Setup(x => x.GetCountAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        mockIntegrityRepo.Setup(x => x.GetWithAuditEventsPagedAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), 0, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([integrityRecord]);
        mockIntegrityRepo.Setup(x => x.GetWithAuditEventsPagedAsync(
                It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.Is<int>(s => s > 0), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var service = new TamperDetectionService(
            mockEventRepo.Object,
            mockIntegrityRepo.Object,
            mockSecurityService.Object,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<TamperDetectionService>.Instance,
            IntegrityTestCrypto.Hasher,
            IntegrityTestCrypto.CreateHmacSigner(HmacKeyBytes, HmacKeyId));

        // Act
        var result = await service.VerifyChainIntegrityAsync();

        // Assert
        Assert.That(result.TotalEvents, Is.EqualTo(1));
        Assert.That(result.EventsChecked, Is.EqualTo(1));
        Assert.That(result.TamperedEvents, Has.Count.EqualTo(1));
        Assert.That(result.TamperedEvents[0].Reason, Does.Contain("Audit event missing"));
        Assert.That(result.IsValid, Is.False, "Missing audit event should cause verification to fail");

        // Should have logged a tamper alert
        mockSecurityService.Verify(
            x => x.RecordEventAsync(
                It.Is<SecurityEventDto>(e =>
                    e.EventType == SecurityEventType.AuditTamperAlert &&
                    e.Message!.Contains("Audit event missing")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Finding #7: Malformed JSON detection

    [Test]
    public async Task VerifyIntegrity_MalformedJsonData_DetectsAsTamper()
    {
        // Arrange: Create event + integrity, then corrupt the JSON
        using var context = CreateContext();
        var auditEvent = CreateAuditEvent(jsonData: """{"valid":"json"}""");
        await context.AuditEvents.AddAsync(auditEvent);
        await context.SaveChangesAsync();

        await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: 1);

        // Corrupt the JSON to be unparseable - use parameter to avoid escaping issues
        var malformedJson = "{invalid json that cannot be parsed";
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE \"AuditEvents\" SET \"JsonData\" = {0} WHERE \"EventId\" = {1}",
            malformedJson, auditEvent.EventId);

        // Act
        using var verifyContext = CreateContext();
        var verifyEventRepo = new AuditEventRepository(verifyContext);
        var verifyIntegrityRepo = new AuditIntegrityRepository(verifyContext);
        var mockSecurityService = new Mock<IAuditSecurityEventService>();
        mockSecurityService
            .Setup(static x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SecurityEventDto());

        var verifyService = CreateService(verifyEventRepo, verifyIntegrityRepo, mockSecurityService.Object);
        var isValid = await verifyService.VerifyIntegrityAsync(auditEvent.EventId);

        // Assert
        Assert.That(isValid, Is.False, "Malformed JSON should cause verification to fail");

        // Should have logged a tamper alert for malformed JSON
        mockSecurityService.Verify(
            x => x.RecordEventAsync(
                It.Is<SecurityEventDto>(e =>
                    e.EventType == SecurityEventType.AuditTamperAlert &&
                    e.Message!.Contains("malformed")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Finding #6: Boundary validation

    [Test]
    public async Task ValidateIntegrityChainWithDetails_MissingStartBoundary_DetectsTruncation()
    {
        // Arrange: Create records 2, 3 but query for 1-3 (missing start)
        using var context = CreateContext();
        var events = new List<AuditEventEntity>();
        string? previousHash = null;

        for (int i = 1; i <= 3; i++)
        {
            var auditEvent = CreateAuditEvent(eventType: $"Boundary.Event{i}");
            events.Add(auditEvent);
            await context.AuditEvents.AddAsync(auditEvent);
            await context.SaveChangesAsync();

            await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: i, previousEventHash: previousHash);
            previousHash = ComputeEventHash(auditEvent);
        }

        // Delete sequence 1
        await context.Database.ExecuteSqlRawAsync(
            "DELETE FROM \"AuditIntegrity\" WHERE \"SequenceNumber\" = 1");

        // Act
        using var verifyContext = CreateContext();
        var verifyIntegrityRepo = new AuditIntegrityRepository(verifyContext);
        var result = await verifyIntegrityRepo.ValidateIntegrityChainWithDetailsAsync(1, 3);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Message, Does.Contain("does not match requested start"));
    }

    [Test]
    public async Task ValidateIntegrityChainWithDetails_MissingEndBoundary_DetectsTruncation()
    {
        // Arrange: Create records 1, 2 but query for 1-3 (missing end)
        using var context = CreateContext();
        string? previousHash = null;

        for (int i = 1; i <= 2; i++)
        {
            var auditEvent = CreateAuditEvent(eventType: $"End.Event{i}");
            await context.AuditEvents.AddAsync(auditEvent);
            await context.SaveChangesAsync();

            await SeedIntegrityRecordAsync(context, auditEvent, sequenceNumber: i, previousEventHash: previousHash);
            previousHash = ComputeEventHash(auditEvent);
        }

        // Act
        using var verifyContext = CreateContext();
        var verifyIntegrityRepo = new AuditIntegrityRepository(verifyContext);
        var result = await verifyIntegrityRepo.ValidateIntegrityChainWithDetailsAsync(1, 3);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Message, Does.Contain("does not match requested end"));
    }

    [Test]
    public async Task ValidateIntegrityChainWithDetails_EmptyRange_ReturnsEmptyNotValid()
    {
        // Arrange: Empty database
        using var context = CreateContext();

        // Act
        var integrityRepo = new AuditIntegrityRepository(context);
        var result = await integrityRepo.ValidateIntegrityChainWithDetailsAsync(1, 10);

        // Assert
        Assert.That(result.IsValid, Is.False);
        Assert.That(result.IsEmpty, Is.True);
        Assert.That(result.Message, Does.Contain("No records found"));
    }

    #endregion
}
