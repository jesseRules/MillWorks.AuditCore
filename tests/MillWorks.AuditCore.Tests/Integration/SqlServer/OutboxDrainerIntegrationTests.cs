using System.Diagnostics.Metrics;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Options;
using MillWorks.AuditCore.EntityFramework.Sinks;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Interfaces;
using MillWorks.AuditCore.Services.DeadLetterQueue.Models;
using MillWorks.AuditCore.Services.DistributedLocking.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Sinks;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Integration.SqlServer;

/// <summary>
/// Integration tests for <see cref="TransactionalOutboxSink"/> and
/// <see cref="AuditOutboxDrainer"/>. Tests atomic rollback of outbox + consumer
/// rows, drainer processing, retry exhaustion with DLQ routing, and metric emission.
/// </summary>
[TestFixture]
[Category("Integration")]
[Category("SqlServer")]
public sealed class OutboxDrainerIntegrationTests
{
    private const string OutboxDatabaseName = "MillWorksAuditCoreOutboxTests";

    private string _connectionString = null!;

    [SetUp]
    public async Task SetUpAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            Assert.Inconclusive(
                $"SQL Server integration tests require Docker for Testcontainers. " +
                $"Reason: {SqlServerContainerFixture.DockerSkipReason ?? "unknown"}");
        }

        var builder = new SqlConnectionStringBuilder(SqlServerContainerFixture.ConnectionString)
        {
            InitialCatalog = OutboxDatabaseName
        };
        _connectionString = builder.ConnectionString;

        await DropAndCreateDatabaseAsync();
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (!SqlServerContainerFixture.DockerAvailable)
        {
            return;
        }

        await DropDatabaseIfExistsAsync();
    }

    [Test]
    public async Task ConsumerRollback_AtomicallyRollsBackOutboxRow()
    {
        var accessor = new ConsumerDbContextAccessor();
        var writer = new AuditOutboxWriter(
            accessor,
            Options.Create(new EntityFrameworkOptions { Schema = "dbo" }),
            NullLogger<AuditOutboxWriter>.Instance);
        var sink = new TransactionalOutboxSink(
            writer,
            NullLogger<TransactionalOutboxSink>.Instance);

        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        await using var transaction = await consumerCtx.Database.BeginTransactionAsync();

        using (accessor.SetCurrent(consumerCtx))
        {
            var envelope = new AuditEnvelope
            {
                Kind = AuditEnvelopeKind.EntityChange,
                EntityName = "TestEntity",
                Action = AuditAction.Created,
                UserId = "user-rollback",
                CorrelationId = "corr-rollback",
            };

            await sink.PublishAsync(envelope);

            var countBefore = await consumerCtx.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM [dbo].[AuditOutbox]")
                .SingleAsync();
            Assert.That(countBefore, Is.EqualTo(1), "Outbox row should exist before rollback");
        }

        await transaction.RollbackAsync();

        var countAfter = await consumerCtx.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM [dbo].[AuditOutbox]")
            .SingleAsync();
        Assert.That(countAfter, Is.Zero, "Outbox row must be rolled back atomically with consumer transaction");
    }

    [Test]
    public async Task DrainBatch_MarksCompletedRows()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            UserId = "user-drain",
            CorrelationId = "corr-drain",
        };

        var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await consumerCtx.Database.ExecuteSqlRawAsync(
            "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            Guid.NewGuid(), envelopeJson, 1, 0, DateTimeOffset.UtcNow, 0);

        var opts = CreateSecurityOptions();
        var sp = BuildDrainerServiceProvider(opts, throwOnPublish: false);

        // Verify the AuditDbContext can see the row
        using (var verifyScope = sp.CreateScope())
        {
            var auditCtx = verifyScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var pendingCount = await auditCtx.AuditOutbox
                .Where(o => o.Status == AuditOutboxStatus.Pending)
                .CountAsync();
            Assert.That(pendingCount, Is.EqualTo(1), "AuditDbContext should see 1 pending row before draining");
        }

        var logMessages = new System.Collections.Concurrent.ConcurrentBag<string>();
        var drainerLogger = new TestOutputLogger<AuditOutboxDrainer>(logMessages);
        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            drainerLogger,
            Options.Create(opts));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Catch any unobserved task exceptions
        var drainerTask = drainer.StartAsync(cts.Token);
        _ = drainerTask.ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                logMessages.Add($"DRAINER TASK FAULTED: {t.Exception?.GetBaseException().Message}");
            }
        }, TaskScheduler.Default);

        // Poll until the row changes or timeout
        AuditOutboxStatus finalStatus = AuditOutboxStatus.Pending;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            using var checkScope = sp.CreateScope();
            var auditCtx = checkScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var checkRow = await auditCtx.AuditOutbox.FirstOrDefaultAsync(cancellationToken: cts.Token);
            if (checkRow is not null && checkRow.Status != AuditOutboxStatus.Pending)
            {
                TestContext.Out.WriteLine($"Row changed: Status={checkRow.Status}, AttemptCount={checkRow.AttemptCount}");
                finalStatus = checkRow.Status;
                break;
            }
        }

        await drainer.StopAsync(CancellationToken.None);

        // Print all log messages
        foreach (var msg in logMessages)
        {
            TestContext.Out.WriteLine($"LOG: {msg}");
        }

        if (finalStatus == AuditOutboxStatus.Pending)
        {
            // Still pending - check if there's error info
            using var checkScope = sp.CreateScope();
            var auditCtx = checkScope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var checkRow = await auditCtx.AuditOutbox.FirstAsync();
            Assert.Fail($"Drainer did not process the row within 5s. AttemptCount={checkRow.AttemptCount}, LastError={checkRow.LastError ?? "null"}, LogCount={logMessages.Count}");
        }

        var row = await consumerCtx.Database
            .SqlQueryRaw<OutboxStatusRow>(
                "SELECT Status, CompletedAt, AttemptCount, LastError FROM [dbo].[AuditOutbox]")
            .SingleAsync(cancellationToken: cts.Token);

        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo((int)AuditOutboxStatus.Completed));
            Assert.That(row.CompletedAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task ExhaustedRetries_MarksFailedAndIncrementsMeter()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "FailingEntity",
            Action = AuditAction.Created,
            UserId = "user-fail",
        };

        var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var rowId = Guid.NewGuid();
        await consumerCtx.Database.ExecuteSqlRawAsync(
            "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            rowId, envelopeJson, 1, 0, DateTimeOffset.UtcNow, 4); // 4 attempts already, 1 more = 5 = max

        var opts = CreateSecurityOptions();
        opts.OutboxDrainerMaxAttempts = 5;
        opts.OutboxDrainerRetryBackoff = [TimeSpan.FromMilliseconds(10)];

        long failedCount = 0;
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Name == "audit.outbox.drainer.failed")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name == "audit.outbox.drainer.failed")
                Interlocked.Add(ref failedCount, measurement);
        });
        meterListener.Start();

        var sp = BuildDrainerServiceProvider(opts, throwOnPublish: true);

        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = drainer.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        await drainer.StopAsync(CancellationToken.None);

        var row = await consumerCtx.Database
            .SqlQueryRaw<OutboxStatusRow>(
                "SELECT Status, CompletedAt, AttemptCount, LastError FROM [dbo].[AuditOutbox] WHERE Id = {0}", rowId)
            .SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.Status, Is.EqualTo((int)AuditOutboxStatus.Failed),
                "Row should be marked Failed after exhausting retries");
            Assert.That(row.AttemptCount, Is.EqualTo(5),
                "AttemptCount should equal MaxAttempts");
            Assert.That(failedCount, Is.EqualTo(1),
                "audit.outbox.drainer.failed counter should increment exactly once");
        });
    }

    [Test]
    public async Task FailedRow_SetsNextRetryAt_AndIsSkippedUntilExpired()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "BackoffEntity",
            Action = AuditAction.Created,
            UserId = "user-backoff",
        };

        var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var rowId = Guid.NewGuid();
        await consumerCtx.Database.ExecuteSqlRawAsync(
            "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            rowId, envelopeJson, 1, 0, DateTimeOffset.UtcNow, 0);

        var opts = CreateSecurityOptions();
        opts.OutboxDrainerMaxAttempts = 5;
        opts.OutboxDrainerRetryBackoff = [TimeSpan.FromHours(1)]; // Long backoff so it won't be retried

        var sp = BuildDrainerServiceProvider(opts, throwOnPublish: true);

        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        // First poll: should fail and set NextRetryAt
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = drainer.StartAsync(cts.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await drainer.StopAsync(CancellationToken.None);

        // Verify NextRetryAt was set
        var row = await consumerCtx.Database
            .SqlQueryRaw<BackoffStatusRow>(
                "SELECT AttemptCount, NextRetryAt FROM [dbo].[AuditOutbox] WHERE Id = {0}", rowId)
            .SingleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(row.AttemptCount, Is.EqualTo(1), "AttemptCount should be 1 after first failure");
            Assert.That(row.NextRetryAt, Is.Not.Null, "NextRetryAt should be set after failure");
            Assert.That(row.NextRetryAt!.Value, Is.GreaterThan(DateTimeOffset.UtcNow.AddMinutes(30)),
                "NextRetryAt should be at least 30 minutes in the future (1 hour backoff minus jitter)");
        });

        // Start drainer again and verify it does NOT process the row (backoff not expired)
        var sp2 = BuildDrainerServiceProvider(opts, throwOnPublish: true);
        var drainer2 = new AuditOutboxDrainer(
            sp2.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        _ = drainer2.StartAsync(cts2.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        await drainer2.StopAsync(CancellationToken.None);

        var rowAfterSecondPoll = await consumerCtx.Database
            .SqlQueryRaw<BackoffStatusRow>(
                "SELECT AttemptCount, NextRetryAt FROM [dbo].[AuditOutbox] WHERE Id = {0}", rowId)
            .SingleAsync();

        Assert.That(rowAfterSecondPoll.AttemptCount, Is.EqualTo(1),
            "AttemptCount should still be 1 - row was skipped due to backoff");
    }

    private sealed record BackoffStatusRow(int AttemptCount, DateTimeOffset? NextRetryAt);

    [Test]
    public async Task BatchFailure_DoesNotDuplicateExplicitEvents()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        // Insert 2 explicit events and 2 entity changes
        var explicitEvent1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EventType = "UserLogin",
            EntityName = "Session",
            Action = AuditAction.Created,
            UserId = "user-explicit-1",
        };
        var explicitEvent2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EventType = "UserLogout",
            EntityName = "Session",
            Action = AuditAction.Deleted,
            UserId = "user-explicit-2",
        };
        var entityChange1 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Updated,
            UserId = "user-entity-1",
        };
        var entityChange2 = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "Patient",
            Action = AuditAction.Created,
            UserId = "user-entity-2",
        };

        foreach (var envelope in new[] { explicitEvent1, explicitEvent2, entityChange1, entityChange2 })
        {
            var json = JsonSerializer.Serialize(envelope, jsonOptions);
            await consumerCtx.Database.ExecuteSqlRawAsync(
                "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
                "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
                Guid.NewGuid(), json, 1, 0, DateTimeOffset.UtcNow, 0);
        }

        var opts = CreateSecurityOptions();
        var countingLogger = new CountingAuditLogger();
        var batchFailingWriter = new BatchFailingEntityBatchWriter();
        var sp = BuildDrainerServiceProviderWithCustomServices(opts, countingLogger, batchFailingWriter);

        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = drainer.StartAsync(cts.Token);

        // Wait for processing
        await Task.Delay(TimeSpan.FromMilliseconds(800));
        await drainer.StopAsync(CancellationToken.None);

        // Verify explicit events were logged exactly twice (once each), not four times
        // The bug would cause them to be replayed in the fallback loop after batch failure
        Assert.That(countingLogger.LogAsyncCallCount, Is.EqualTo(2),
            "Explicit events should be logged exactly once each, not duplicated on batch fallback");

        // Verify entity changes were written (batch failed, then succeeded one-at-a-time)
        Assert.That(batchFailingWriter.SingleEnvelopeCallCount, Is.EqualTo(2),
            "Entity changes should fall back to one-at-a-time after batch failure");

        // Verify all rows completed
        var completedCount = await consumerCtx.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM [dbo].[AuditOutbox] WHERE Status = {(int)AuditOutboxStatus.Completed}")
            .SingleAsync();
        Assert.That(completedCount, Is.EqualTo(4), "All 4 rows should be marked Completed");
    }

    [Test]
    public async Task EnvelopeId_PreservedThroughOutboxAndDrainer()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var originalEnvelopeId = Guid.NewGuid();
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "EnvelopeIdTestEntity",
            Action = AuditAction.Created,
            UserId = "user-envelope-id",
            EnvelopeId = originalEnvelopeId,
        };

        var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        // Verify the JSON contains the envelopeId
        Assert.That(envelopeJson, Does.Contain("\"envelopeId\""));
        Assert.That(envelopeJson, Does.Contain(originalEnvelopeId.ToString()));

        await consumerCtx.Database.ExecuteSqlRawAsync(
            "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            Guid.NewGuid(), envelopeJson, 1, 0, DateTimeOffset.UtcNow, 0);

        var opts = CreateSecurityOptions();
        var capturedEnvelopes = new System.Collections.Concurrent.ConcurrentBag<AuditEnvelope>();
        var sp = BuildDrainerServiceProviderWithEnvelopeCapture(opts, capturedEnvelopes);

        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = drainer.StartAsync(cts.Token);

        // Wait for processing
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && capturedEnvelopes.IsEmpty)
        {
            await Task.Delay(50);
        }

        await drainer.StopAsync(CancellationToken.None);

        Assert.That(capturedEnvelopes.Count, Is.EqualTo(1), "Drainer should have processed exactly one envelope");
        var capturedEnvelope = capturedEnvelopes.First();
        Assert.Multiple(() =>
        {
            Assert.That(capturedEnvelope.EnvelopeId, Is.EqualTo(originalEnvelopeId),
                "EnvelopeId must survive outbox serialization and drainer deserialization");
            Assert.That(capturedEnvelope.EntityName, Is.EqualTo("EnvelopeIdTestEntity"));
            Assert.That(capturedEnvelope.UserId, Is.EqualTo("user-envelope-id"));
        });
    }

    [Test]
    public async Task ExhaustedRetries_RoutesToDlq()
    {
        await using var consumerCtx = CreateConsumerContext();
        await consumerCtx.Database.EnsureCreatedAsync();

        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.EntityChange,
            EntityName = "DlqEntity",
            Action = AuditAction.Deleted,
            UserId = "user-dlq",
        };

        var envelopeJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        await consumerCtx.Database.ExecuteSqlRawAsync(
            "INSERT INTO [dbo].[AuditOutbox] (Id, EnvelopeJson, EnvelopeVersion, Status, CreatedAt, AttemptCount) " +
            "VALUES ({0}, {1}, {2}, {3}, {4}, {5})",
            Guid.NewGuid(), envelopeJson, 1, 0, DateTimeOffset.UtcNow, 4);

        var opts = CreateSecurityOptions();
        opts.OutboxDrainerMaxAttempts = 5;

        var dlq = new RecordingDlq();
        var sp = BuildDrainerServiceProvider(opts, throwOnPublish: true, dlq: dlq);

        var drainer = new AuditOutboxDrainer(
            sp.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AuditOutboxDrainer>.Instance,
            Options.Create(opts));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        _ = drainer.StartAsync(cts.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(800));
        await drainer.StopAsync(CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(dlq.StoredEvents.Count, Is.EqualTo(1), "One event should be routed to DLQ");
            Assert.That(dlq.StoredEvents[0].EntityName, Does.Contain("AuditOutbox"),
                "DLQ event EntityName should reference AuditOutbox");
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private OutboxConsumerDbContext CreateConsumerContext()
    {
        var options = new DbContextOptionsBuilder<OutboxConsumerDbContext>()
            .UseSqlServer(_connectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;
        return new OutboxConsumerDbContext(options);
    }

    private static SecurityOptions CreateSecurityOptions() => new()
    {
        AuditSinkMode = AuditSinkMode.TransactionalOutbox,
        OutboxDrainerPollInterval = TimeSpan.FromMilliseconds(100),
        OutboxDrainerBatchSize = 10,
        OutboxDrainerMaxAttempts = 5,
        OutboxDrainerRetryBackoff = [TimeSpan.FromMilliseconds(10)],
        OutboxDrainerBackoffJitterRatio = 0,
        OutboxDrainerCircuitBreakerThreshold = 100,
        OutboxDrainerCircuitBreakerSleep = TimeSpan.FromSeconds(1),
    };

    private IServiceProvider BuildDrainerServiceProvider(
        SecurityOptions opts,
        bool throwOnPublish,
        RecordingDlq? dlq = null)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(opts));
        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = "dbo" });
        services.AddSingleton(efOptions);
        services.AddScoped<AuditDbContext>(sp =>
        {
            var dbOptions = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlServer(_connectionString)
                .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
                .Options;
            return new AuditDbContext(dbOptions, encryptionService: null, efOptions: efOptions);
        });

        services.AddScoped<IAuditDistributedLockService, NoOpLockService>();

        services.AddScoped<IAuditLogger, NoOpAuditLogger>();

        if (throwOnPublish)
        {
            services.AddScoped<IAuditEntityBatchWriter, ThrowingAuditEntityBatchWriter>();
        }
        else
        {
            services.AddScoped<IAuditEntityBatchWriter, NoOpAuditEntityBatchWriter>();
        }

        services.AddScoped<IAuditEventBatchWriter, NoOpAuditEventBatchWriter>();
        services.AddScoped<ImmediateSink>();

        if (dlq is not null)
        {
            services.AddSingleton<IAuditDeadLetterQueue>(dlq);
        }

        return services.BuildServiceProvider();
    }

    private IServiceProvider BuildDrainerServiceProviderWithCustomServices(
        SecurityOptions opts,
        IAuditLogger auditLogger,
        IAuditEntityBatchWriter entityBatchWriter)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(opts));
        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = "dbo" });
        services.AddSingleton(efOptions);
        services.AddScoped<AuditDbContext>(sp =>
        {
            var dbOptions = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlServer(_connectionString)
                .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
                .Options;
            return new AuditDbContext(dbOptions, encryptionService: null, efOptions: efOptions);
        });

        services.AddScoped<IAuditDistributedLockService, NoOpLockService>();
        services.AddSingleton(auditLogger);
        services.AddSingleton(entityBatchWriter);
        services.AddScoped<IAuditEventBatchWriter, NoOpAuditEventBatchWriter>();
        services.AddScoped<ImmediateSink>();

        return services.BuildServiceProvider();
    }

    private IServiceProvider BuildDrainerServiceProviderWithEnvelopeCapture(
        SecurityOptions opts,
        System.Collections.Concurrent.ConcurrentBag<AuditEnvelope> capturedEnvelopes)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddSingleton(Options.Create(opts));
        var efOptions = Options.Create(new EntityFrameworkOptions { Schema = "dbo" });
        services.AddSingleton(efOptions);
        services.AddScoped<AuditDbContext>(sp =>
        {
            var dbOptions = new DbContextOptionsBuilder<AuditDbContext>()
                .UseSqlServer(_connectionString)
                .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
                .Options;
            return new AuditDbContext(dbOptions, encryptionService: null, efOptions: efOptions);
        });

        services.AddScoped<IAuditDistributedLockService, NoOpLockService>();
        services.AddSingleton<IAuditLogger, NoOpAuditLogger>();
        services.AddSingleton<IAuditEntityBatchWriter>(new CapturingAuditEntityBatchWriter(capturedEnvelopes));
        services.AddScoped<IAuditEventBatchWriter, NoOpAuditEventBatchWriter>();
        services.AddScoped<ImmediateSink>();

        return services.BuildServiceProvider();
    }

    private async Task DropAndCreateDatabaseAsync()
    {
        await DropDatabaseIfExistsAsync();
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE [{OutboxDatabaseName}];";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task DropDatabaseIfExistsAsync()
    {
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        cmd.CommandText = $"""
            IF DB_ID('{OutboxDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{OutboxDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{OutboxDatabaseName}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private static SqlConnection CreateMasterConnection()
    {
        var builder = new SqlConnectionStringBuilder(SqlServerContainerFixture.ConnectionString)
        {
            InitialCatalog = "master"
        };
        return new SqlConnection(builder.ConnectionString);
    }

    // ── Test fixture types ───────────────────────────────────────────────────

    private sealed class OutboxConsumerDbContext : DbContext
    {
        public OutboxConsumerDbContext(DbContextOptions<OutboxConsumerDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<AuditOutboxEntity>(e =>
            {
                e.ToTable("AuditOutbox", "dbo");
                e.HasKey(x => x.Id);
                e.Property(x => x.EnvelopeJson).IsRequired();
                e.Property(x => x.EnvelopeVersion).HasDefaultValue(1);
                e.Property(x => x.Status).HasDefaultValue(AuditOutboxStatus.Pending);
                e.Property(x => x.CreatedAt).HasDefaultValueSql("GETUTCDATE()");
                e.Property(x => x.NextRetryAt);
                e.Property(x => x.LastError).HasMaxLength(2000);
                e.HasIndex(x => x.Status);
                e.HasIndex(x => x.CreatedAt);
                e.HasIndex(x => new { x.Status, x.NextRetryAt, x.CreatedAt });
            });
        }
    }

    private sealed record OutboxStatusRow(int Status, DateTimeOffset? CompletedAt, int AttemptCount = 0, string? LastError = null);

    private sealed class NoOpLockService : IAuditDistributedLockService
    {
        public Task<IDisposable> AcquireLockAsync(
            string lockName,
            TimeSpan expiry,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IDisposable>(new NoOpDisposable());

        public Task<IDisposable?> TryAcquireLockAsync(
            string lockName,
            TimeSpan expiry,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IDisposable?>(new NoOpDisposable());

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class NoOpAuditEntityBatchWriter : IAuditEntityBatchWriter
    {
        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken = default)
        {
            var outcomes = envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList();
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    private sealed class NoOpAuditEventBatchWriter : IAuditEventBatchWriter
    {
        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken = default)
        {
            var outcomes = envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList();
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    private sealed class CapturingAuditEntityBatchWriter : IAuditEntityBatchWriter
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<AuditEnvelope> _captured;

        public CapturingAuditEntityBatchWriter(System.Collections.Concurrent.ConcurrentBag<AuditEnvelope> captured)
        {
            _captured = captured;
        }

        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken = default)
        {
            var outcomes = new List<WriteOutcome>();
            foreach (var envelope in envelopes)
            {
                _captured.Add(envelope);
                outcomes.Add(WriteOutcome.Success(envelope.EnvelopeId));
            }
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    private sealed class ThrowingAuditEntityBatchWriter : IAuditEntityBatchWriter
    {
        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated drain failure");
    }

    private sealed class BatchFailingEntityBatchWriter : IAuditEntityBatchWriter
    {
        private int _singleEnvelopeCallCount;
        public int SingleEnvelopeCallCount => _singleEnvelopeCallCount;

        public Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
            IReadOnlyList<AuditEnvelope> envelopes,
            CancellationToken cancellationToken = default)
        {
            if (envelopes.Count > 1)
                throw new InvalidOperationException("Simulated batch failure");

            Interlocked.Increment(ref _singleEnvelopeCallCount);
            var outcomes = envelopes.Select(e => WriteOutcome.Success(e.EnvelopeId)).ToList();
            return Task.FromResult<IReadOnlyList<WriteOutcome>>(outcomes);
        }
    }

    private sealed class CountingAuditLogger : IAuditLogger
    {
        private int _logAsyncCallCount;
        public int LogAsyncCallCount => _logAsyncCallCount;

        public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _logAsyncCallCount);
            return Task.CompletedTask;
        }

        public Task<BatchAuditResult> LogBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchAuditResult());

        public Task LogAsync(string eventType, object? data = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task LogAsync(string eventType, string message, Dictionary<string, object?> data, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Guid> BeginOperationAsync(string operationType, object? metadata = null)
            => Task.FromResult(Guid.NewGuid());

        public Task EndOperationAsync(Guid operationId, bool success = true, object? result = null)
            => Task.CompletedTask;

        public ICustomAuditScope CreateScope(string eventType, object? target = null)
            => Mock.Of<ICustomAuditScope>();
    }

    private sealed class NoOpAuditLogger : IAuditLogger
    {
        public Task LogAsync(AuditEvent auditEvent, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<BatchAuditResult> LogBatchAsync(IReadOnlyList<AuditEvent> auditEvents, CancellationToken cancellationToken = default)
            => Task.FromResult(new BatchAuditResult());

        public Task LogAsync(string eventType, object? data = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task LogAsync(string eventType, string message, Dictionary<string, object?> data, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<Guid> BeginOperationAsync(string operationType, object? metadata = null)
            => Task.FromResult(Guid.NewGuid());

        public Task EndOperationAsync(Guid operationId, bool success = true, object? result = null)
            => Task.CompletedTask;

        public ICustomAuditScope CreateScope(string eventType, object? target = null)
            => Mock.Of<ICustomAuditScope>();
    }

    private sealed class RecordingDlq : IAuditDeadLetterQueue
    {
        public List<AuditEvent> StoredEvents { get; } = [];

        public Task StoreFailedEventAsync(AuditEvent auditEvent, Exception? exception = null, string? reason = null)
        {
            StoredEvents.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task StoreFailedEntityAsync(AuditEventEntity entity, Exception? exception = null, string? reason = null)
            => Task.CompletedTask;

        public Task<List<DeadLetterAuditEvent>> GetFailedEventsAsync(int maxCount = 100)
            => Task.FromResult(new List<DeadLetterAuditEvent>());

        public Task<List<DeadLetterAuditEvent>> GetFailedEventsByDateAsync(DateTimeOffset startDate, DateTimeOffset endDate)
            => Task.FromResult(new List<DeadLetterAuditEvent>());

        public Task<bool> ReprocessEventAsync(string deadLetterId)
            => Task.FromResult(false);

        public Task<ReprocessingResult> ReprocessAllAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ReprocessingResult());

        public Task<int> PurgeProcessedEventsAsync()
            => Task.FromResult(0);

        public Task<DeadLetterStatistics> GetStatisticsAsync()
            => Task.FromResult(new DeadLetterStatistics());
    }

    private sealed class TestOutputLogger<T> : ILogger<T>
    {
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _messages;

        public TestOutputLogger(System.Collections.Concurrent.ConcurrentBag<string> messages)
        {
            _messages = messages;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            _messages.Add($"[{logLevel}] {msg}");
            if (exception is not null)
            {
                _messages.Add($"[{logLevel}] EXCEPTION: {exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
