using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Tests.Services;

[TestFixture]
public sealed class RequestAuditProcessorTests
{
    [Test]
    public async Task ProcessAsync_WhenAuditLoggerSucceeds_CompletesNormally()
    {
        var mockLogger = new Mock<IAuditLogger>();
        mockLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var processor = new RequestAuditProcessor(
            mockLogger.Object,
            NullLogger<RequestAuditProcessor>.Instance);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid() };

        await processor.ProcessAsync(auditEvent, CancellationToken.None);

        mockLogger.Verify(x => x.LogAsync(auditEvent, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProcessAsync_WhenAuditLoggerFails_NoDlq_Throws()
    {
        var mockLogger = new Mock<IAuditLogger>();
        mockLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB connection failed"));

        var processor = new RequestAuditProcessor(
            mockLogger.Object,
            NullLogger<RequestAuditProcessor>.Instance,
            deadLetterQueue: null);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid() };

        Assert.That(
            async () => await processor.ProcessAsync(auditEvent, CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>()
                .With.Message.EqualTo("DB connection failed"));
    }

    [Test]
    public async Task ProcessAsync_WhenAuditLoggerFails_WithDlq_RoutesToDlq()
    {
        var mockLogger = new Mock<IAuditLogger>();
        var originalException = new InvalidOperationException("DB connection failed");
        mockLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(originalException);

        var mockDlq = new Mock<IAuditDeadLetterQueue>();
        mockDlq
            .Setup(x => x.StoreFailedEventAsync(
                It.IsAny<AuditEvent>(),
                It.IsAny<Exception?>(),
                It.IsAny<string?>()))
            .Returns(Task.CompletedTask);

        var processor = new RequestAuditProcessor(
            mockLogger.Object,
            NullLogger<RequestAuditProcessor>.Instance,
            mockDlq.Object);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid() };

        await processor.ProcessAsync(auditEvent, CancellationToken.None);

        mockDlq.Verify(x => x.StoreFailedEventAsync(
            auditEvent,
            originalException,
            "Deferred request audit persistence failed"),
            Times.Once);
    }

    [Test]
    public void ProcessAsync_WhenAuditLoggerFails_AndDlqFails_ThrowsAggregateException()
    {
        var mockLogger = new Mock<IAuditLogger>();
        var originalException = new InvalidOperationException("DB connection failed");
        mockLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(originalException);

        var dlqException = new IOException("Disk full");
        var mockDlq = new Mock<IAuditDeadLetterQueue>();
        mockDlq
            .Setup(x => x.StoreFailedEventAsync(
                It.IsAny<AuditEvent>(),
                It.IsAny<Exception?>(),
                It.IsAny<string?>()))
            .ThrowsAsync(dlqException);

        var processor = new RequestAuditProcessor(
            mockLogger.Object,
            NullLogger<RequestAuditProcessor>.Instance,
            mockDlq.Object);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid() };

        var ex = Assert.ThrowsAsync<AggregateException>(
            async () => await processor.ProcessAsync(auditEvent, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.InnerExceptions, Has.Count.EqualTo(2));
            Assert.That(ex.InnerExceptions[0], Is.SameAs(originalException));
            Assert.That(ex.InnerExceptions[1], Is.SameAs(dlqException));
            Assert.That(ex.Message, Does.Contain(auditEvent.EventId.ToString()));
        });
    }

    [Test]
    public void ProcessAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var mockLogger = new Mock<IAuditLogger>();
        mockLogger
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var processor = new RequestAuditProcessor(
            mockLogger.Object,
            NullLogger<RequestAuditProcessor>.Instance);

        var auditEvent = new AuditEvent { EventId = Guid.NewGuid() };

        Assert.That(
            async () => await processor.ProcessAsync(auditEvent, CancellationToken.None),
            Throws.TypeOf<OperationCanceledException>());
    }
}
