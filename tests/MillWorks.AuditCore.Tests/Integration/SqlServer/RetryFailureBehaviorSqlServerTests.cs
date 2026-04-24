using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.SqlServer.Infrastructure.Internal;
using Microsoft.EntityFrameworkCore.SqlServer.Storage.Internal;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.DeadLetterQueue.Services;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

public sealed class RetryFailureBehaviorSqlServerTests
{
    [Test]
    public void UseEntityFramework_DoesNotEnableSqlServerRetryStrategy()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddMillWorksAudit(builder =>
        {
            // Development environment + HmacKey satisfies AuditOptionsValidator without
            // triggering the Production-mode "HmacKey required" failure.
            builder.Options.Environment = "Development";
            builder.Options.HmacKey = "test-hmac-key-retry-strategy-assertion-32";
            builder.UseEntityFramework(opts =>
            {
                opts.ConnectionString = "Server=(localdb);Database=Fake;";
            });
        });

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();

        var strategy = ctx.Database.CreateExecutionStrategy();

        Assert.Multiple(() =>
        {
            Assert.That(strategy.RetriesOnFailure, Is.False,
                "SQL Server provider must not enable RetryOnFailure; explicit transactions in tamper-detection / archival / compliance paths are incompatible with SqlServerRetryingExecutionStrategy.");
            Assert.That(strategy, Is.Not.InstanceOf<SqlServerRetryingExecutionStrategy>(),
                "Expected the default NonRetryingExecutionStrategy, not SqlServerRetryingExecutionStrategy.");
        });
    }

    [Test]
    public async Task ResilientAuditLogger_TransientWriteFailure_RoutesToDeadLetterQueueAfterRetries()
    {
        var failingInner = new Mock<IAuditLogger>();
        failingInner
            .Setup(x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient write failure"));

        var fieldRedactor = new Mock<IAuditFieldRedactor>();
        fieldRedactor
            .Setup(x => x.RedactFields(It.IsAny<Dictionary<string, object?>>()))
            .Returns<Dictionary<string, object?>>(d => d);
        fieldRedactor
            .Setup(x => x.RedactValue(It.IsAny<string>(), It.IsAny<string?>()))
            .Returns<string, string?>((_, v) => v);
        fieldRedactor
            .Setup(x => x.RedactTarget(It.IsAny<AuditTarget?>()))
            .Returns<AuditTarget?>(t => t);

        using var dlqServiceProvider = new ServiceCollection().BuildServiceProvider();
        var deadLetterQueue = new InMemoryAuditDeadLetterQueue(
            NullLogger<InMemoryAuditDeadLetterQueue>.Instance,
            dlqServiceProvider,
            fieldRedactor.Object);

        var eventFactory = new Mock<IAuditEventFactory>();

        using var scopeProvider = ScopeFactoryForwardingAuditLogger.BuildProviderForwarding(failingInner.Object);
        var resilientLogger = new ResilientAuditLogger(
            innerLogger: failingInner.Object,
            deadLetterQueue: deadLetterQueue,
            eventFactory: eventFactory.Object,
            fieldRedactor: fieldRedactor.Object,
            scopeFactory: scopeProvider.GetRequiredService<IServiceScopeFactory>(),
            logger: NullLogger<ResilientAuditLogger>.Instance);

        var testEvent = new AuditEvent { EventType = "RetryTest" };
        var testEventId = testEvent.EventId;

        await resilientLogger.LogAsync(testEvent);

        var dlqEvents = await deadLetterQueue.GetFailedEventsAsync();

        Assert.Multiple(() =>
        {
            failingInner.Verify(
                x => x.LogAsync(It.IsAny<AuditEvent>(), It.IsAny<CancellationToken>()),
                Times.Exactly(4),
                "Inner logger should be invoked 4 times: initial attempt + 3 retries.");
            Assert.That(dlqEvents, Has.Count.EqualTo(1),
                "DLQ should contain exactly one entry after retry exhaustion.");
            Assert.That(dlqEvents[0].OriginalEvent?.EventId, Is.EqualTo(testEventId),
                "DLQ entry should carry the original event id.");
            Assert.That(dlqEvents[0].FailureReason, Does.Contain("Failed after"),
                "DLQ entry should record the retry-exhaustion reason.");
        });
    }
}
