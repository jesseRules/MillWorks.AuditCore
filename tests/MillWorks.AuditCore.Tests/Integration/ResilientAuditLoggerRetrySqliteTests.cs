using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.AspNetCore.Extensions;  // provides services.Decorate<TInterface, TDecorator>()
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Core;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Services;
using MillWorks.AuditCore.Services.Diagnostics;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Regression for the scoped-ChangeTracker identity-map bug on
/// <see cref="ResilientAuditLogger"/> retry. Without scope-per-retry, the first
/// attempt's <c>AuditEventEntity</c> stays tracked on the scoped DbContext after
/// <see cref="AuditLogger"/>'s <c>ExecuteInTransactionAsync</c> rolls back, and the
/// second attempt's <c>AddAsync</c> on a new <c>AuditEventEntity</c> instance with
/// the same <c>EventId</c> throws <c>InvalidOperationException</c> ("The instance of
/// entity type 'AuditEventEntity' cannot be tracked because another instance with the
/// same key value for {'EventId'} is already being tracked"), masking the original
/// failure and driving every event through to the DLQ.
/// </summary>
[TestFixture]
[Category("Integration")]
public sealed class ResilientAuditLoggerRetrySqliteTests : SqliteIntegrationFixture
{
    [Test]
    public async Task LogAsync_TransientTamperFailureOnFirstAttempt_RetrySucceeds_NoTrackingConflict()
    {
        var flaky = new FailFirstTamperDetectionService();
        var fieldRedactor = new PassThroughAuditFieldRedactor();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));

        services.AddOptions<AuditOptions>().Configure(o =>
        {
            o.Environment = "Development";
            o.HmacKey = "retry-tracking-regression-test-hmac-32";
        });
        services.AddOptions<SecurityOptions>().Configure(o => o.EnableTamperDetection = true);

        // Re-bind the fixture's SQLite connection into the DI-managed context so that
        // every DI-created scope (including ResilientAuditLogger's per-retry scope)
        // points at the same in-memory database as the verification context.
        services.AddDbContext<AuditDbContext>(opts => opts.UseSqlite(Connection));

        services.AddScoped<IAuditEventRepository, AuditEventRepository>();
        services.AddScoped<IAuditIntegrityRepository, AuditIntegrityRepository>();

        services.AddSingleton<ITamperDetectionService>(flaky);
        services.AddSingleton<IAuditFieldRedactor>(fieldRedactor);
        services.AddScoped<IAuditContext>(_ => Mock.Of<IAuditContext>());
        services.AddSingleton<IAuditEventFactory>(Mock.Of<IAuditEventFactory>());
        services.AddSingleton<IAuditDiagnostics, AuditDiagnostics>();

        var dlqProvider = new ServiceCollection().BuildServiceProvider();
        services.AddSingleton<IAuditDeadLetterQueue>(new InMemoryAuditDeadLetterQueue(
            NullLogger<InMemoryAuditDeadLetterQueue>.Instance,
            dlqProvider,
            fieldRedactor));

        // Match the production registration: AuditLogger concrete + IAuditLogger forwarder.
        // ResilientAuditLogger resolves AuditLogger concretely from each retry scope.
        services.AddScoped<AuditLogger>();
        services.AddScoped<IAuditLogger>(sp => sp.GetRequiredService<AuditLogger>());

        services.Decorate<IAuditLogger, ResilientAuditLogger>();

        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true });

        using (var seedScope = provider.CreateScope())
        {
            // Ensure the fixture-created schema matches the DI-created context.
            await seedScope.ServiceProvider.GetRequiredService<AuditDbContext>()
                .Database.EnsureCreatedAsync();
        }

        using var requestScope = provider.CreateScope();
        var resilientLogger = requestScope.ServiceProvider.GetRequiredService<IAuditLogger>();
        Assert.That(resilientLogger, Is.TypeOf<ResilientAuditLogger>(),
            "Decoration must wrap IAuditLogger so retries exercise ResilientAuditLogger.LogAsync.");

        var auditEvent = new AuditEvent
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Retry.NoTrackingConflict",
            StartDate = DateTimeOffset.UtcNow,
            EndDate = DateTimeOffset.UtcNow
        };

        // Act — should fail first attempt, succeed on retry, NOT throw "already tracked".
        await resilientLogger.LogAsync(auditEvent);

        // Assert — verify the retry path through a fresh scope committed the event
        // exactly once and never fell through to the DLQ. The stub replaces the real
        // integrity writer, so we don't assert on the integrity table here — Bug 2 is
        // specifically about the ChangeTracker conflict on the event insert, not the
        // integrity chain (that's Bug 1, covered by AuditLoggerTamperNestedTransactionTests).
        using var verifyContext = CreateContext();
        var events = await verifyContext.AuditEvents
            .AsNoTracking()
            .Where(e => e.EventId == auditEvent.EventId)
            .ToListAsync();

        var dlq = requestScope.ServiceProvider.GetRequiredService<IAuditDeadLetterQueue>();
        var dlqEntries = await dlq.GetFailedEventsAsync();

        Assert.Multiple(() =>
        {
            Assert.That(flaky.CallCount, Is.GreaterThanOrEqualTo(2),
                "Pre-fix: attempt 2 would blow up in AddAsync with 'already tracked' before reaching " +
                "tamper detection, so CallCount would stick at 1. Post-fix: attempt 2 runs in a fresh " +
                "scope, passes AddAsync, and invokes tamper detection again.");
            Assert.That(events, Has.Count.EqualTo(1),
                "Exactly one AuditEventEntity row should be committed by the successful retry. " +
                "Pre-fix would leave this empty (every attempt's transaction rolls back).");
            Assert.That(dlqEntries, Is.Empty,
                "A successful retry must not dead-letter the event. Pre-fix, all 4 attempts fail with " +
                "'already tracked' and the event ends up in the DLQ.");
        });
    }

    /// <summary>
    /// Test double that throws on its first <see cref="CreateIntegrityRecordAsync"/> call
    /// and delegates to a minimal real implementation on subsequent calls. Simulates a
    /// transient tamper-detection failure that causes <c>AuditLogger</c>'s outer
    /// <c>ExecuteInTransactionAsync</c> to roll back mid-flow, which on the pre-fix
    /// <c>ResilientAuditLogger</c> leaves the rolled-back <c>AuditEventEntity</c>
    /// tracked for the next retry.
    /// </summary>
    private sealed class FailFirstTamperDetectionService : ITamperDetectionService
    {
        private int _callCount;
        public int CallCount => _callCount;

        public Task<AuditIntegrityDto> CreateIntegrityRecordAsync(
            AuditIntegrityDto auditEvent,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            if (call == 1)
                throw new InvalidOperationException("Simulated transient tamper-detection failure");
            return Task.FromResult(new AuditIntegrityDto { EventId = auditEvent.EventId });
        }

        public Task<IReadOnlyList<AuditIntegrityDto>> CreateIntegrityRecordBatchAsync(
            IReadOnlyList<AuditIntegrityDto> auditEvents,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifyIntegrityAsync(Guid eventId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<TamperDetectionResult> VerifyChainIntegrityAsync(
            DateTimeOffset? startDate = null,
            DateTimeOffset? endDate = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> VerifySequenceIntegrityAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<List<TamperAlert>> DetectTamperingAsync(
            int hoursBack = 24,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<byte[]> ExportIntegrityProofAsync(
            Guid eventId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
