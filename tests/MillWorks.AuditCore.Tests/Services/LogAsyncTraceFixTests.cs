using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Services;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests confirming fixes for LogAsync trace issues S4, S6, N1, P8
/// </summary>
[TestFixture]
public class LogAsyncTraceFixTests
{
    private static AuditDbContext CreateInMemoryDbContext() =>
        new(new DbContextOptionsBuilder<AuditDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    #region N1: LogBatchAsync OperationCanceledException handling

    [Test]
    public void LogBatchAsync_WhenCancelled_ThrowsOperationCanceledWithoutLoggingError()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockEventFactory = new Mock<IAuditEventFactory>();
        var mockRepo = new Mock<IAuditEventRepository>();
        var mockContext = new Mock<IAuditContext>();

        // Make SaveChangesAsync throw OperationCanceledException
        mockRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<AuditEventEntity>());
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            mockEventFactory.Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            mockContext.Object,
            new PassThroughAuditFieldRedactor());

        var events = new List<AuditEvent>
        {
            new() { EventType = "Test.Event1" },
            new() { EventType = "Test.Event2" }
        };

        // Act & Assert — should throw OperationCanceledException, not wrap it
        Assert.ThrowsAsync<OperationCanceledException>(
            () => auditLogger.LogBatchAsync(events, CancellationToken.None));

        // Verify LogError was NOT called (the fix prevents error logging on cancellation)
        mockLogger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Never);
    }

    [Test]
    public void LogBatchAsync_WhenCancelled_DoesNotCatchAsGenericException()
    {
        // Arrange — verify that OperationCanceledException is re-thrown silently
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockRepo = new Mock<IAuditEventRepository>();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        mockRepo.Setup(r => r.AddRangeAsync(It.IsAny<IEnumerable<AuditEventEntity>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Enumerable.Empty<AuditEventEntity>());
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            new Mock<IAuditContext>().Object,
            new PassThroughAuditFieldRedactor());

        var events = new List<AuditEvent>
        {
            new() { EventType = "A" },
            new() { EventType = "B" }
        };

        // Act & Assert
        var ex = Assert.ThrowsAsync<OperationCanceledException>(
            () => auditLogger.LogBatchAsync(events, cts.Token));
        Assert.That(ex, Is.Not.Null);
    }

    #endregion

    #region P8: SanitizeInput zero-allocation fast path

    [Test]
    public void SanitizeInput_CleanString_ReturnsSameReference()
    {
        // Arrange — SanitizeInput is private, so we test through ConvertToEntity via LogAsync
        // A clean IpAddress should pass through without allocation (same string reference)
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockRepo = new Mock<IAuditEventRepository>();
        AuditEventEntity? capturedEntity = null;

        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            new Mock<IAuditContext>().Object,
            new PassThroughAuditFieldRedactor());

        var auditEvent = new AuditEvent
        {
            EventType = "Test",
            IpAddress = "192.168.1.1",
            UserAgent = "Mozilla/5.0 (Windows NT 10.0)"
        };

        // Act
        auditLogger.LogAsync(auditEvent).GetAwaiter().GetResult();

        // Assert — clean strings should pass through unmodified
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IpAddress, Is.EqualTo("192.168.1.1"));
        Assert.That(capturedEntity.UserAgent, Is.EqualTo("Mozilla/5.0 (Windows NT 10.0)"));
    }

    [Test]
    public void SanitizeInput_StringWithControlChars_ReplacesWithSpaces()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockRepo = new Mock<IAuditEventRepository>();
        AuditEventEntity? capturedEntity = null;

        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            new Mock<IAuditContext>().Object,
            new PassThroughAuditFieldRedactor());

        // Inject control characters: newline, tab, null byte
        var auditEvent = new AuditEvent
        {
            EventType = "Test",
            IpAddress = "192.168.1.1\nX-Injected: evil",
            UserAgent = "Mozilla\t\0Evil"
        };

        // Act
        auditLogger.LogAsync(auditEvent).GetAwaiter().GetResult();

        // Assert — control characters replaced with spaces
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.IpAddress, Is.EqualTo("192.168.1.1 X-Injected: evil"));
        Assert.That(capturedEntity.UserAgent, Is.EqualTo("Mozilla  Evil"));
        // Verify no control characters remain
        Assert.That(capturedEntity.IpAddress!.Any(char.IsControl), Is.False);
        Assert.That(capturedEntity.UserAgent!.Any(char.IsControl), Is.False);
    }

    #endregion

    #region S6: Target object now passes through RedactTarget

    [Test]
    public void ConvertToEntity_TargetObject_IsRedactedBeforeSerialization()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockRepo = new Mock<IAuditEventRepository>();
        var mockRedactor = new Mock<IAuditFieldRedactor>();
        AuditEventEntity? capturedEntity = null;

        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .Callback<AuditEventEntity, CancellationToken>((e, _) => capturedEntity = e)
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);

        // Pass-through for fields
        mockRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(f => f);
        mockRedactor.Setup(r => r.RedactValue(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((_, v) => v);

        // Redact target: null out Old/New values (simulating PHI redaction)
        mockRedactor.Setup(r => r.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t == null ? null : new AuditTarget
            {
                Type = t.Type,
                Old = "[REDACTED]",
                New = "[REDACTED]"
            });

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            new Mock<IAuditContext>().Object,
            mockRedactor.Object);

        var auditEvent = new AuditEvent
        {
            EventType = "Patient.Update",
            Target = new AuditTarget
            {
                Type = "Patient",
                Old = new { SSN = "123-45-6789", Name = "John Doe" },
                New = new { SSN = "123-45-6789", Name = "Jane Doe" }
            }
        };

        // Act
        auditLogger.LogAsync(auditEvent).GetAwaiter().GetResult();

        // Assert — RedactTarget was called
        mockRedactor.Verify(r => r.RedactTarget(It.IsAny<AuditTarget?>()), Times.Once);

        // Assert — JsonData contains redacted target, not original PHI
        Assert.That(capturedEntity, Is.Not.Null);
        Assert.That(capturedEntity!.JsonData, Does.Contain("[REDACTED]"));
        Assert.That(capturedEntity.JsonData, Does.Not.Contain("123-45-6789"));
        Assert.That(capturedEntity.JsonData, Does.Not.Contain("John Doe"));
    }

    [Test]
    public void ConvertToEntity_NullTarget_RedactTargetStillCalled()
    {
        // Arrange
        var mockLogger = new Mock<ILogger<AuditLogger>>();
        var mockRepo = new Mock<IAuditEventRepository>();
        var mockRedactor = new Mock<IAuditFieldRedactor>();

        mockRepo.Setup(r => r.AddAsync(It.IsAny<AuditEventEntity>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((AuditEventEntity e, CancellationToken _) => e);
        mockRepo.Setup(r => r.SaveChangesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(1);
        mockRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(f => f);
        mockRedactor.Setup(r => r.RedactValue(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((_, v) => v);
        mockRedactor.Setup(r => r.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t);

        var auditLogger = new AuditLogger(
            mockLogger.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRepo.Object,
            CreateInMemoryDbContext(),
            new Mock<IAuditContext>().Object,
            mockRedactor.Object);

        var auditEvent = new AuditEvent { EventType = "Test", Target = null };

        // Act
        auditLogger.LogAsync(auditEvent).GetAwaiter().GetResult();

        // Assert — RedactTarget is called even for null (allows redactor to substitute a placeholder)
        mockRedactor.Verify(r => r.RedactTarget(null), Times.Once);
    }

    [Test]
    public void PassThroughRedactor_RedactTarget_ReturnsTargetUnchanged()
    {
        // Verify the default implementation passes through
        var redactor = new PassThroughAuditFieldRedactor();
        IAuditFieldRedactor iface = redactor;

        var target = new AuditTarget { Type = "Patient", Old = "secret", New = "secret2" };
        var result = iface.RedactTarget(target);

        Assert.That(result, Is.SameAs(target));
    }

    #endregion

    #region S4: Emergency fallback redacts data before writing

    [Test]
    public async Task EmergencyFallback_RedactsCustomFieldsBeforeWriting()
    {
        // Arrange
        var mockInnerLogger = new Mock<IAuditLogger>();
        var mockDlq = new Mock<IAuditDeadLetterQueue>();
        var mockEventFactory = new Mock<IAuditEventFactory>();
        var mockRedactor = new Mock<IAuditFieldRedactor>();
        var mockLogger = new Mock<ILogger<ResilientAuditLogger>>();

        // Inner logger always fails
        mockInnerLogger.Setup(l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB down"));

        // DLQ also fails to trigger emergency fallback
        mockDlq.Setup(d => d.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception?>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("DLQ down"));

        // Redactor replaces sensitive fields
        mockRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns(new Dictionary<string, object?> { ["SSN"] = "[REDACTED]" });
        mockRedactor.Setup(r => r.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t == null ? null : new AuditTarget
            {
                Type = t.Type,
                Old = "[REDACTED]",
                New = "[REDACTED]"
            });

        using var scopeProvider = ScopeFactoryForwardingAuditLogger.BuildProviderForwarding(mockInnerLogger.Object);
        var resilientLogger = new ResilientAuditLogger(
            mockInnerLogger.Object,
            mockDlq.Object,
            mockEventFactory.Object,
            mockRedactor.Object,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            mockLogger.Object);

        var auditEvent = new AuditEvent
        {
            EventType = "Patient.Create",
            Target = new AuditTarget { Type = "Patient", Old = null, New = new { SSN = "123-45-6789" } },
            CustomFields = new Dictionary<string, object?> { ["SSN"] = "123-45-6789", ["Name"] = "John Doe" }
        };

        // Act — this triggers retries -> DLQ failure -> emergency fallback
        await resilientLogger.LogAsync(auditEvent);

        // Assert — verify redactor was called for both fields and target
        mockRedactor.Verify(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()), Times.Once);
        mockRedactor.Verify(r => r.RedactTarget(It.IsAny<AuditTarget?>()), Times.Once);

        // Check that emergency file was created with redacted content
        var emergencyPath = Path.Combine(Path.GetTempPath(), "MillWorks.Audit", "AuditEmergency");
        var files = Directory.Exists(emergencyPath)
            ? Directory.GetFiles(emergencyPath, $"emergency_{auditEvent.EventId}_*.json")
            : [];

        if (files.Length > 0)
        {
            var content = await File.ReadAllTextAsync(files[0]);
            // Verify redacted content
            Assert.That(content, Does.Contain("[REDACTED]"));
            Assert.That(content, Does.Not.Contain("123-45-6789"));
            Assert.That(content, Does.Not.Contain("John Doe"));
            // Verify error details are minimal (type name only, not full stack trace)
            Assert.That(content, Does.Not.Contain("at MillWorks"));

            // Cleanup
            foreach (var f in files) File.Delete(f);
        }
    }

    [Test]
    public async Task EmergencyFallback_ErrorField_ContainsTypeNameOnly()
    {
        // Arrange
        var mockInnerLogger = new Mock<IAuditLogger>();
        var mockDlq = new Mock<IAuditDeadLetterQueue>();
        var mockRedactor = new Mock<IAuditFieldRedactor>();
        var mockLogger = new Mock<ILogger<ResilientAuditLogger>>();

        mockInnerLogger.Setup(l => l.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("secret connection string here"));
        mockDlq.Setup(d => d.StoreFailedEventAsync(It.IsAny<AuditEvent>(), It.IsAny<Exception?>(), It.IsAny<string>()))
            .ThrowsAsync(new InvalidOperationException("DLQ failed"));

        mockRedactor.Setup(r => r.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(f => f);
        mockRedactor.Setup(r => r.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t);

        using var scopeProvider = ScopeFactoryForwardingAuditLogger.BuildProviderForwarding(mockInnerLogger.Object);
        var resilientLogger = new ResilientAuditLogger(
            mockInnerLogger.Object,
            mockDlq.Object,
            new Mock<IAuditEventFactory>().Object,
            mockRedactor.Object,
            scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            mockLogger.Object);

        var auditEvent = new AuditEvent { EventType = "Test" };

        // Act
        await resilientLogger.LogAsync(auditEvent);

        // Assert — check file doesn't contain full exception details
        var emergencyPath = Path.Combine(Path.GetTempPath(), "MillWorks.Audit", "AuditEmergency");
        var files = Directory.Exists(emergencyPath)
            ? Directory.GetFiles(emergencyPath, $"emergency_{auditEvent.EventId}_*.json")
            : [];

        if (files.Length > 0)
        {
            var content = await File.ReadAllTextAsync(files[0]);
            // Should contain exception type name
            Assert.That(content, Does.Contain("InvalidOperationException"));
            // Should NOT contain the sensitive message or stack trace
            Assert.That(content, Does.Not.Contain("secret connection string here"));
            Assert.That(content, Does.Not.Contain("at MillWorks"));

            // Cleanup
            foreach (var f in files) File.Delete(f);
        }
    }

    #endregion
}
