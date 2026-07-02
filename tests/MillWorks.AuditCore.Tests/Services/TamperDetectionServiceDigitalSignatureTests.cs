using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests for TamperDetectionService digital-signature paths, cancellation handling, and
/// LogTamperAlertAsync behaviour. After the MillWorks.Cryptography extraction the RSA-PSS signature
/// is produced/verified by an RSA-PSS <see cref="MillWorks.Cryptography.Signing.ISigner"/> over an
/// <see cref="MillWorks.Cryptography.KeyManagement.ISigningKeyProvider"/>; the persisted key id makes
/// verification reselect the exact key, so key isolation is exercised with two distinct RSA keys
/// rather than two PEM file paths. The Production fail-closed behaviour of the key backend is covered
/// at the DI layer (see OptionsFlowTests.Production_NoIntegrityMasterKey_FailsWhenSignerResolved).
/// </summary>
[TestFixture]
[Category("Unit")]
public sealed class TamperDetectionServiceDigitalSignatureTests : IDisposable
{
    private Mock<IAuditEventRepository> _mockAuditEventRepository;
    private Mock<IAuditIntegrityRepository> _mockAuditIntegrityRepository;
    private Mock<IAuditSecurityEventService> _mockSecurityEventService;
    private Mock<ILogger<TamperDetectionService>> _mockLogger;

    // Shared HMAC key/id across services in this fixture so cross-service verification fails ONLY on
    // the digital signature (the HMAC, checked first, stays valid).
    private static readonly byte[] SharedHmacKey = RandomNumberGenerator.GetBytes(32);
    private const string SharedHmacKeyId = "ds-tests-hmac-v1";

    private RSA _rsa = null!;

    [SetUp]
    public void Setup()
    {
        _mockAuditEventRepository = new Mock<IAuditEventRepository>();
        _mockAuditIntegrityRepository = new Mock<IAuditIntegrityRepository>();
        _mockSecurityEventService = new Mock<IAuditSecurityEventService>();
        _mockLogger = new Mock<ILogger<TamperDetectionService>>();

        // Auto-invoke the transaction lambda so per-test Get/Add/SaveChanges setups run.
        _mockAuditIntegrityRepository
            .Setup(x => x.ExecuteInTransactionAsync(It.IsAny<Func<Task>>(), It.IsAny<CancellationToken>()))
            .Returns<Func<Task>, CancellationToken>((action, _) => action());

        _mockAuditIntegrityRepository
            .Setup(static x => x.AcquireAppendLockAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _mockAuditIntegrityRepository
            .SetupGet(static x => x.SupportsCrossProcessAppendLock)
            .Returns(true);

        _rsa = RSA.Create(2048);
    }

    public void Dispose() => _rsa?.Dispose();

    /// <summary>Builds a service with digital signatures enabled over the given (or default) RSA key.</summary>
    private TamperDetectionService CreateServiceWithSignatures(RSA? rsa = null, string rsaKeyId = "ds-tests-rsa-v1")
    {
        return new TamperDetectionService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockSecurityEventService.Object,
            _mockLogger.Object,
            IntegrityTestCrypto.Hasher,
            IntegrityTestCrypto.CreateHmacSigner(SharedHmacKey, SharedHmacKeyId),
            IntegrityTestCrypto.CreateRsaSigner(rsa ?? _rsa, rsaKeyId));
    }

    /// <summary>Builds a service with digital signatures disabled (no RSA signer).</summary>
    private TamperDetectionService CreateService()
    {
        return new TamperDetectionService(
            _mockAuditEventRepository.Object,
            _mockAuditIntegrityRepository.Object,
            _mockSecurityEventService.Object,
            _mockLogger.Object,
            IntegrityTestCrypto.Hasher,
            IntegrityTestCrypto.CreateHmacSigner(SharedHmacKey, SharedHmacKeyId));
    }

    private void SetupRepositoryForCreate(Guid eventId)
    {
        _mockAuditIntegrityRepository
            .Setup(static x => x.GetLatestBySequenceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditIntegrityEntity?)null);

        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        _mockAuditIntegrityRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        _mockAuditIntegrityRepository
            .Setup(static x => x.ClearChangeTrackerAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    #region Digital Signature — CreateIntegrityRecordAsync

    [Test]
    public async Task CreateIntegrityRecordAsync_WithDigitalSignaturesEnabled_PopulatesSignatureField()
    {
        var eventId = Guid.NewGuid();
        var service = CreateServiceWithSignatures();
        SetupRepositoryForCreate(eventId);

        AuditIntegrityEntity? captured = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        await service.CreateIntegrityRecordAsync(new AuditIntegrityDto { EventId = eventId });

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DigitalSignature, Is.Not.Null.And.Not.Empty,
            "DigitalSignature should be populated when digital signatures are enabled");
        Assert.That(captured.DigitalSignatureKeyId, Is.EqualTo("ds-tests-rsa-v1"),
            "The signing key id should be persisted alongside the signature");
        Assert.DoesNotThrow(() => Convert.FromBase64String(captured.DigitalSignature!));
    }

    [Test]
    public async Task CreateIntegrityRecordAsync_WithDigitalSignaturesDisabled_LeavesSignatureNull()
    {
        var eventId = Guid.NewGuid();
        var service = CreateService();
        SetupRepositoryForCreate(eventId);

        AuditIntegrityEntity? captured = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        await service.CreateIntegrityRecordAsync(new AuditIntegrityDto { EventId = eventId });

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.DigitalSignature, Is.Null);
        Assert.That(captured.DigitalSignatureKeyId, Is.Null);
    }

    #endregion

    #region Digital Signature — Verification Round-Trip

    [Test]
    public async Task VerifyIntegrityAsync_WithValidDigitalSignature_ReturnsTrue()
    {
        var eventId = Guid.NewGuid();
        var fixedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = CreateServiceWithSignatures();
        SetupRepositoryForCreate(eventId);

        AuditIntegrityEntity? captured = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        var dto = new AuditIntegrityDto
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };
        await service.CreateIntegrityRecordAsync(dto);

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(captured!);

        var result = await service.VerifyIntegrityAsync(eventId);

        Assert.That(result, Is.True);
    }

    [Test]
    public async Task VerifyIntegrityAsync_WithCorruptedDigitalSignature_ReturnsFalse()
    {
        var eventId = Guid.NewGuid();
        var fixedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = CreateServiceWithSignatures();
        SetupRepositoryForCreate(eventId);

        AuditIntegrityEntity? captured = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        var dto = new AuditIntegrityDto
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };
        await service.CreateIntegrityRecordAsync(dto);

        // Corrupt the digital signature (still valid Base64 so it reaches the RSA verify path).
        var corruptedBytes = Convert.FromBase64String(captured!.DigitalSignature!);
        corruptedBytes[0] ^= 0xFF;
        captured.DigitalSignature = Convert.ToBase64String(corruptedBytes);

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(captured);

        var result = await service.VerifyIntegrityAsync(eventId);

        Assert.That(result, Is.False);
        _mockSecurityEventService.Verify(
            x => x.RecordEventAsync(
                It.Is<SecurityEventDto>(e =>
                    e.EventType == SecurityEventType.AuditTamperAlert &&
                    e.Message!.Contains("Digital signature invalid")),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    #endregion

    #region Digital Signature — Batch Path

    [Test]
    public async Task CreateIntegrityRecordBatchAsync_WithDigitalSignatures_SignsAllRecords()
    {
        var service = CreateServiceWithSignatures();
        var events = Enumerable.Range(0, 3)
            .Select(_ => new AuditIntegrityDto { EventId = Guid.NewGuid() })
            .ToList();

        _mockAuditIntegrityRepository
            .Setup(static x => x.GetLatestBySequenceAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditIntegrityEntity?)null);

        var capturedEntities = new List<AuditIntegrityEntity>();
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddRangeAsync(It.IsAny<IEnumerable<AuditIntegrityEntity>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<AuditIntegrityEntity>, CancellationToken>((entities, _) =>
                capturedEntities.AddRange(entities))
            .ReturnsAsync(static (IEnumerable<AuditIntegrityEntity> e, CancellationToken _) => e);

        _mockAuditIntegrityRepository
            .Setup(static x => x.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var results = await service.CreateIntegrityRecordBatchAsync(events);

        Assert.That(results, Has.Count.EqualTo(3));
        Assert.That(capturedEntities, Has.Count.EqualTo(3));

        foreach (var entity in capturedEntities)
        {
            Assert.That(entity.DigitalSignature, Is.Not.Null.And.Not.Empty,
                $"Event {entity.EventId} should have a digital signature");
            Assert.That(entity.DigitalSignatureKeyId, Is.EqualTo("ds-tests-rsa-v1"));
            Assert.DoesNotThrow(() => Convert.FromBase64String(entity.DigitalSignature!));
        }
    }

    #endregion

    #region Key Isolation — Distinct RSA Keys

    [Test]
    public async Task TwoInstances_WithDifferentRsaKeys_ProduceDistinctSignaturesAndDoNotCrossVerify()
    {
        using var rsa1 = RSA.Create(2048);
        using var rsa2 = RSA.Create(2048);

        // Same HMAC key (so the HMAC, checked first, passes for both) but distinct RSA keys/ids, so a
        // cross-instance verification fails specifically on the digital signature.
        var service1 = CreateServiceWithSignatures(rsa1, "rsa-key-1");
        var service2 = CreateServiceWithSignatures(rsa2, "rsa-key-2");

        var eventId1 = Guid.NewGuid();
        var eventId2 = Guid.NewGuid();
        var fixedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        AuditIntegrityEntity? captured1 = null;
        AuditIntegrityEntity? captured2 = null;

        SetupRepositoryForCreate(eventId1);
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) =>
            {
                if (e.EventId == eventId1) captured1 = e;
                else if (e.EventId == eventId2) captured2 = e;
            })
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        var dto1 = new AuditIntegrityDto
        {
            EventId = eventId1, EventType = "Test.Event", User = "user1", InsertedDate = fixedDate, JsonData = "{}"
        };
        var dto2 = new AuditIntegrityDto
        {
            EventId = eventId2, EventType = "Test.Event", User = "user2", InsertedDate = fixedDate, JsonData = "{}"
        };

        await service1.CreateIntegrityRecordAsync(dto1);
        await service2.CreateIntegrityRecordAsync(dto2);

        Assert.That(captured1, Is.Not.Null);
        Assert.That(captured2, Is.Not.Null);
        Assert.That(captured1!.DigitalSignature, Is.Not.EqualTo(captured2!.DigitalSignature),
            "Different keys should produce different signatures");
        Assert.That(captured1.DigitalSignatureKeyId, Is.EqualTo("rsa-key-1"));
        Assert.That(captured2.DigitalSignatureKeyId, Is.EqualTo("rsa-key-2"));

        // service1 verifies its own signature.
        var auditEvent1 = new AuditEventEntity
        {
            EventId = eventId1, EventType = "Test.Event", User = "user1", InsertedDate = fixedDate, JsonData = "{}"
        };
        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent1);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(captured1);

        Assert.That(await service1.VerifyIntegrityAsync(eventId1), Is.True,
            "Service1 should verify its own signature");

        // service2 cannot verify service1's signature — its RSA provider does not hold key id "rsa-key-1".
        Assert.That(await service2.VerifyIntegrityAsync(eventId1), Is.False,
            "Service2 should not verify service1's signature (different key)");
    }

    #endregion

    #region LogTamperAlertAsync — Security Event Structure

    [Test]
    public async Task VerifyIntegrityAsync_HashMismatch_LogsSecurityEventWithCorrectStructure()
    {
        var eventId = Guid.NewGuid();
        var service = CreateService();

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{}"
        };

        var integrity = new AuditIntegrityEntity
        {
            EventId = eventId,
            EventHash = "deliberately-wrong-hash",
            HmacSignature = "hmac",
            Checksum = "chk",
            AlgorithmVersion = 1
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrity);

        SecurityEventDto? capturedEvent = null;
        _mockSecurityEventService
            .Setup(x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .Callback<SecurityEventDto, CancellationToken>((e, _) => capturedEvent = e)
            .ReturnsAsync(static (SecurityEventDto e, CancellationToken _) => e);

        var result = await service.VerifyIntegrityAsync(eventId);

        Assert.That(result, Is.False);
        Assert.That(capturedEvent, Is.Not.Null);
        Assert.That(capturedEvent!.EventType, Is.EqualTo(SecurityEventType.AuditTamperAlert));
        Assert.That(capturedEvent.Severity, Is.EqualTo(SecurityEventSeverity.Critical));
        Assert.That(capturedEvent.RelatedAuditEventId, Is.EqualTo(eventId));
        Assert.That(capturedEvent.Message, Does.Contain(eventId.ToString()));
        Assert.That(capturedEvent.Details, Does.ContainKey("EventId"));
        Assert.That(capturedEvent.Details, Does.ContainKey("Reason"));
        Assert.That(capturedEvent.Details, Does.ContainKey("DetectionMethod"));
        Assert.That(capturedEvent.Details, Does.ContainKey("Timestamp"));
    }

    [Test]
    public async Task VerifyIntegrityAsync_WhenSecurityEventServiceThrows_StillReturnsFalse()
    {
        var eventId = Guid.NewGuid();
        var service = CreateService();

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = DateTimeOffset.UtcNow,
            JsonData = "{}"
        };

        var integrity = new AuditIntegrityEntity
        {
            EventId = eventId,
            EventHash = "deliberately-wrong-hash",
            HmacSignature = "hmac",
            Checksum = "chk",
            AlgorithmVersion = 1
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(integrity);

        _mockSecurityEventService
            .Setup(x => x.RecordEventAsync(It.IsAny<SecurityEventDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Security event service is down"));

        var result = await service.VerifyIntegrityAsync(eventId);

        Assert.That(result, Is.False);
    }

    #endregion

    #region CancellationToken Propagation

    [Test]
    public void CreateIntegrityRecordAsync_WithCancelledToken_ThrowsOperationCancelledException()
    {
        var service = CreateService();

        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.CatchAsync<OperationCanceledException>(
            () => service.CreateIntegrityRecordAsync(
                new AuditIntegrityDto { EventId = Guid.NewGuid() },
                cts.Token));
    }

    #endregion

    #region Algorithm Version Mismatch Warning

    [Test]
    public async Task VerifyIntegrityAsync_WithMismatchedAlgorithmVersion_StillVerifies()
    {
        var eventId = Guid.NewGuid();
        var fixedDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var service = CreateService();

        SetupRepositoryForCreate(eventId);

        AuditIntegrityEntity? captured = null;
        _mockAuditIntegrityRepository
            .Setup(static x => x.AddAsync(It.IsAny<AuditIntegrityEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditIntegrityEntity, CancellationToken>((e, _) => captured = e)
            .ReturnsAsync(static (AuditIntegrityEntity e, CancellationToken _) => e);

        var dto = new AuditIntegrityDto
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };
        await service.CreateIntegrityRecordAsync(dto);

        captured!.AlgorithmVersion = 999;

        var auditEvent = new AuditEventEntity
        {
            EventId = eventId,
            EventType = "Test.Event",
            User = "testuser",
            InsertedDate = fixedDate,
            JsonData = "{}"
        };

        _mockAuditEventRepository
            .Setup(x => x.GetByIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(auditEvent);
        _mockAuditIntegrityRepository
            .Setup(x => x.GetByEventIdAsync(eventId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(captured);

        var result = await service.VerifyIntegrityAsync(eventId);

        Assert.That(result, Is.True);
    }

    #endregion
}
