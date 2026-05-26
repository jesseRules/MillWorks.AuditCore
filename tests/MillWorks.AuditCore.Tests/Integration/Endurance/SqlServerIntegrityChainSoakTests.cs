using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.DeadLetterQueue.Implementations;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Options;
using MillWorks.AuditCore.Services.TamperDetection;
using Testcontainers.MsSql;

namespace MillWorks.AuditCore.Tests.Integration.Endurance;

// Phase 6.5 soak harness. Opt-in via AUDITCORE_RUN_ENDURANCE=1; otherwise Inconclusive.
// Not under Integration.SqlServer.* so the sql-integration CI lane filter excludes it.
// Owns its own Testcontainers SQL Server lifecycle (per Jesse's option-4 ruling) rather
// than sharing Integration.SqlServer's [SetUpFixture], because NUnit [SetUpFixture] is
// namespace-scoped — sibling namespaces do not inherit it.
public sealed class SqlServerIntegrityChainSoakTests
{
    private const string SoakDatabaseName = "MillWorksAuditCoreSoakTests";
    private const int TotalEvents = 100_000;
    private const int SeedChunkSize = 1_000;
    private const int IntegrityBatchSize = 1_000;
    private const int ConcurrentWriters = 4;
    private const long MemoryCapBytes = 750L * 1024 * 1024;
    private const string HmacKey = "sql-server-soak-100k-endurance-test-hmac-key";
    private const string OptInEnvVar = "AUDITCORE_RUN_ENDURANCE";

    private MsSqlContainer? _container;
    private string? _containerConnectionString;
    private string _soakConnectionString = null!;
    private string _artifactsDir = null!;
    private DateTimeOffset _runStartedAt;

    [OneTimeSetUp]
    public async Task OneTimeSetUpAsync()
    {
        if (Environment.GetEnvironmentVariable(OptInEnvVar) != "1")
        {
            Assert.Inconclusive(
                $"Phase 6.5 endurance soak is opt-in. Set {OptInEnvVar}=1 to run; " +
                $"use `dotnet test -e {OptInEnvVar}=1 ...` so the value reaches the NUnit worker.");
        }

        try
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")
                .WithPassword("AuditCore_Test_Password_123!")
                .Build();
            await _container.StartAsync();

            var raw = _container.GetConnectionString();
            var builder = new SqlConnectionStringBuilder(raw)
            {
                TrustServerCertificate = true,
                InitialCatalog = "master"
            };
            _containerConnectionString = builder.ConnectionString;
        }
        catch (Exception ex) when (IsDockerUnavailable(ex))
        {
            await TestContext.Progress.WriteLineAsync(
                $"[EnduranceSoak] Docker unavailable, Phase 6.5 soak marked Inconclusive: {ex.Message}");
            Assert.Inconclusive(
                $"SQL Server endurance soak requires Docker for Testcontainers. Reason: {ex.Message}");
        }
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDownAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
            _container = null;
        }
    }

    [SetUp]
    public async Task SetUpAsync()
    {
        var builder = new SqlConnectionStringBuilder(_containerConnectionString!)
        {
            InitialCatalog = SoakDatabaseName
        };
        _soakConnectionString = builder.ConnectionString;

        await DropDatabaseIfExistsAsync();
        await using (var master = CreateMasterConnection())
        {
            await master.OpenAsync();
            await using var cmd = master.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE [{SoakDatabaseName}];";
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var ctx = CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
        }

        _runStartedAt = DateTimeOffset.UtcNow;
        _artifactsDir = Path.Combine(
            ResolveRepositoryRoot(),
            "artifacts",
            "phase6.5-soak",
            _runStartedAt.ToString("yyyy-MM-ddTHH-mm-ss'Z'", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(_artifactsDir);
    }

    [TearDown]
    public async Task TearDownAsync()
    {
        if (_containerConnectionString is null)
        {
            return;
        }

        // Clear pooled connections so DROP DATABASE (via SINGLE_USER) doesn't race
        // with EF's pool.
        SqlConnection.ClearAllPools();
        await DropDatabaseIfExistsAsync();
    }

    [Test]
    [CancelAfter(900_000)]
    public async Task Soak_100kAuditEvents_ChainRemainsValidAndMemoryStaysBounded()
    {
        var stopwatch = Stopwatch.StartNew();
        var samples = new List<MemorySample>(capacity: 64);

        var startBytes = GC.GetTotalMemory(forceFullCollection: true);
        samples.Add(new MemorySample("baseline", "pre-seed", stopwatch.Elapsed, startBytes));

        // Single captured timestamp for all events. VerifyChainIntegrityAsync re-hashes the
        // AuditEvent it reads back from the datetimeoffset column; sub-tick precision drift
        // between writer-side DTO.InsertedDate and verifier-side re-read would break hashes.
        var insertedDate = DateTimeOffset.UtcNow;
        var allEventDtos = await SeedAuditEventsAsync(insertedDate, stopwatch, samples);

        var afterSeedBytes = GC.GetTotalMemory(forceFullCollection: false);
        samples.Add(new MemorySample("baseline", "post-seed", stopwatch.Elapsed, afterSeedBytes));

        await WriteIntegrityChainConcurrentlyAsync(allEventDtos, stopwatch, samples);

        var afterWriteBytes = GC.GetTotalMemory(forceFullCollection: false);
        samples.Add(new MemorySample("baseline", "post-integrity-write", stopwatch.Elapsed, afterWriteBytes));

        var deadLetterQueue = new InMemoryAuditDeadLetterQueue(
            NullLogger<InMemoryAuditDeadLetterQueue>.Instance,
            new ServiceCollection().BuildServiceProvider(),
            new Mock<IAuditFieldRedactor>().Object);

        TamperDetectionResult verifyResult;
        await using (var verifyContext = CreateContext())
        {
            var verifyService = CreateService(verifyContext);
            verifyResult = await verifyService.VerifyChainIntegrityAsync(startDate: null, endDate: null);
        }

        var afterVerifyBytes = GC.GetTotalMemory(forceFullCollection: false);
        samples.Add(new MemorySample("baseline", "post-verify", stopwatch.Elapsed, afterVerifyBytes));

        var dlqStats = await deadLetterQueue.GetStatisticsAsync();
        var dlqCount = dlqStats.TotalEvents;

        var finalBytes = GC.GetTotalMemory(forceFullCollection: true);
        samples.Add(new MemorySample("baseline", "final-forced-gc", stopwatch.Elapsed, finalBytes));
        stopwatch.Stop();

        await WriteArtifactsAsync(samples, verifyResult, dlqCount, startBytes, finalBytes, stopwatch.Elapsed);

        Assert.Multiple(() =>
        {
            Assert.That(verifyResult.IsValid, Is.True,
                "100k-row chain should verify clean.");
            Assert.That(verifyResult.ChainBroken, Is.False,
                "Chain continuity should hold across all concurrent-writer batch boundaries.");
            Assert.That(verifyResult.EventsChecked, Is.EqualTo(TotalEvents),
                "Verifier should have visited every integrity row.");
            Assert.That(verifyResult.TotalEvents, Is.EqualTo(TotalEvents),
                "Integrity count should match events written.");
            Assert.That(verifyResult.TamperedEvents, Is.Empty,
                "No row should be flagged as tampered on expected-success traffic.");
            Assert.That(dlqCount, Is.EqualTo(0),
                "Expected-success traffic must not route to the DLQ.");
            Assert.That(finalBytes - startBytes, Is.LessThanOrEqualTo(MemoryCapBytes),
                $"Final forced-GC managed memory grew by more than {MemoryCapBytes / (1024 * 1024)} MB " +
                "above start, suggesting unbounded retention.");
        });
    }

    private async Task<List<AuditIntegrityDto>> SeedAuditEventsAsync(
        DateTimeOffset insertedDate,
        Stopwatch stopwatch,
        List<MemorySample> samples)
    {
        const string stableUser = "phase6.5-soak";
        var stableUserId = Guid.NewGuid();

        var dtos = new List<AuditIntegrityDto>(TotalEvents);
        var chunkCount = TotalEvents / SeedChunkSize;

        for (int chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
        {
            var chunkEntities = new List<AuditEventEntity>(SeedChunkSize);
            for (int j = 0; j < SeedChunkSize; j++)
            {
                var rowIndex = chunkIndex * SeedChunkSize + j;
                chunkEntities.Add(new AuditEventEntity
                {
                    EventType = $"Soak.Event.{rowIndex}",
                    JsonData = $"{{\"index\":{rowIndex}}}",
                    User = stableUser,
                    UserId = stableUserId,
                    InsertedDate = insertedDate
                });
            }

            await using (var seedContext = CreateContext())
            {
                seedContext.AuditEvents.AddRange(chunkEntities);
                await seedContext.SaveChangesAsync();
            }

            foreach (var entity in chunkEntities)
            {
                dtos.Add(new AuditIntegrityDto
                {
                    EventId = entity.EventId,
                    EventType = entity.EventType,
                    User = entity.User,
                    UserId = entity.UserId,
                    JsonData = entity.JsonData,
                    InsertedDate = entity.InsertedDate
                });
            }

            if ((chunkIndex + 1) % 10 == 0)
            {
                var bytes = GC.GetTotalMemory(forceFullCollection: false);
                samples.Add(new MemorySample(
                    "seed",
                    $"after-{(chunkIndex + 1) * SeedChunkSize}",
                    stopwatch.Elapsed,
                    bytes));
            }
        }

        return dtos;
    }

    private async Task WriteIntegrityChainConcurrentlyAsync(
        IReadOnlyList<AuditIntegrityDto> allEventDtos,
        Stopwatch stopwatch,
        List<MemorySample> samples)
    {
        var batches = new List<List<AuditIntegrityDto>>(TotalEvents / IntegrityBatchSize);
        for (int start = 0; start < allEventDtos.Count; start += IntegrityBatchSize)
        {
            batches.Add(allEventDtos.Skip(start).Take(IntegrityBatchSize).ToList());
        }

        var completedBatches = 0;
        var samplesLock = new object();

        await Parallel.ForEachAsync(
            batches,
            new ParallelOptions { MaxDegreeOfParallelism = ConcurrentWriters },
            async (batch, ct) =>
            {
                await using var writerContext = CreateContext();
                var writerService = CreateService(writerContext);
                await writerService.CreateIntegrityRecordBatchAsync(batch, ct);

                var done = Interlocked.Increment(ref completedBatches);
                if (done % 10 == 0)
                {
                    var bytes = GC.GetTotalMemory(forceFullCollection: false);
                    var sample = new MemorySample(
                        "integrity-write",
                        $"after-{done * IntegrityBatchSize}",
                        stopwatch.Elapsed,
                        bytes);
                    lock (samplesLock)
                    {
                        samples.Add(sample);
                    }
                }
            });
    }

    private async Task WriteArtifactsAsync(
        IReadOnlyList<MemorySample> samples,
        TamperDetectionResult verifyResult,
        long dlqCount,
        long startBytes,
        long finalBytes,
        TimeSpan totalElapsed)
    {
        var samplesPath = Path.Combine(_artifactsDir, "samples.csv");
        await using (var samplesWriter = new StreamWriter(samplesPath, append: false, Encoding.UTF8))
        {
            await samplesWriter.WriteLineAsync("phase,sample_label,elapsed_seconds,gc_bytes,gc_mb");
            foreach (var sample in samples)
            {
                var mb = sample.Bytes / (1024.0 * 1024.0);
                await samplesWriter.WriteLineAsync(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{sample.Phase},{sample.Label},{sample.Elapsed.TotalSeconds:F3},{sample.Bytes},{mb:F2}"));
            }
        }

        var notesPath = Path.Combine(_artifactsDir, "notes.md");
        await using var notesWriter = new StreamWriter(notesPath, append: false, Encoding.UTF8);
        await notesWriter.WriteLineAsync("# Phase 6.5 — SQL Server Integrity Chain Soak");
        await notesWriter.WriteLineAsync();
        await notesWriter.WriteLineAsync($"- Run started: {_runStartedAt:O}");
        await notesWriter.WriteLineAsync($"- Total elapsed: {totalElapsed.TotalSeconds:F1} s");
        await notesWriter.WriteLineAsync($"- Host processor count: {Environment.ProcessorCount}");
        await notesWriter.WriteLineAsync($"- Concurrent writers (fixed): {ConcurrentWriters}");
        await notesWriter.WriteLineAsync($"- Events seeded: {TotalEvents:N0} in chunks of {SeedChunkSize:N0}");
        await notesWriter.WriteLineAsync($"- Integrity batches: {TotalEvents / IntegrityBatchSize:N0} × {IntegrityBatchSize:N0}");
        await notesWriter.WriteLineAsync($"- OS / runtime: {Environment.OSVersion} / {Environment.Version}");
        await notesWriter.WriteLineAsync();
        await notesWriter.WriteLineAsync("## Chain verification");
        await notesWriter.WriteLineAsync($"- IsValid: {verifyResult.IsValid}");
        await notesWriter.WriteLineAsync($"- ChainBroken: {verifyResult.ChainBroken}");
        await notesWriter.WriteLineAsync($"- EventsChecked: {verifyResult.EventsChecked:N0}");
        await notesWriter.WriteLineAsync($"- TotalEvents: {verifyResult.TotalEvents:N0}");
        await notesWriter.WriteLineAsync($"- TamperedEvents: {verifyResult.TamperedEvents.Count}");
        await notesWriter.WriteLineAsync();
        await notesWriter.WriteLineAsync("## Dead-letter queue");
        await notesWriter.WriteLineAsync($"- Entries after run: {dlqCount}");
        await notesWriter.WriteLineAsync(
            "- Note: the C1/L1 path (direct EF seeding + TamperDetectionService.CreateIntegrityRecordBatchAsync) " +
            "does not wire the DLQ into any failure edge. Asserting 0 here proves the acceptance contract " +
            "literally; it is not a regression gate against the logger/batcher failure paths.");
        await notesWriter.WriteLineAsync();
        await notesWriter.WriteLineAsync("## Memory");
        var capMb = MemoryCapBytes / (1024 * 1024);
        var startMb = startBytes / (1024.0 * 1024.0);
        var finalMb = finalBytes / (1024.0 * 1024.0);
        var deltaMb = (finalBytes - startBytes) / (1024.0 * 1024.0);
        await notesWriter.WriteLineAsync($"- Start (forced GC): {startMb:F2} MB");
        await notesWriter.WriteLineAsync($"- Final (forced GC): {finalMb:F2} MB");
        await notesWriter.WriteLineAsync($"- Delta: {deltaMb:F2} MB");
        await notesWriter.WriteLineAsync($"- Hard cap: {capMb} MB (delta must be ≤ cap)");
        await notesWriter.WriteLineAsync();
        await notesWriter.WriteLineAsync("## Files");
        await notesWriter.WriteLineAsync("- `samples.csv` — every GC sample captured during the run.");
    }

    private AuditDbContext CreateContext()
    {
        // Mirrors SqlServerContainerFixture.CreateContext + MillWorksAuditBuilder.cs:173 so
        // the schema-aware model cache behaves identically to production.
        var options = new DbContextOptionsBuilder<AuditDbContext>()
            .UseSqlServer(_soakConnectionString)
            .ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()
            .Options;
        return new AuditDbContext(options);
    }

    private static TamperDetectionService CreateService(AuditDbContext context)
    {
        var eventRepo = new AuditEventRepository(context);
        var integrityRepo = new AuditIntegrityRepository(context);
        var securityEventService = new Mock<IAuditSecurityEventService>().Object;

        return new TamperDetectionService(
            eventRepo,
            integrityRepo,
            securityEventService,
            NullLogger<TamperDetectionService>.Instance,
            Options.Create(new AuditOptions
            {
                Environment = "Development",
                HmacKey = HmacKey
            }),
            Options.Create(new SecurityOptions()));
    }

    private async Task DropDatabaseIfExistsAsync()
    {
        await using var master = CreateMasterConnection();
        await master.OpenAsync();
        await using var cmd = master.CreateCommand();
        // SINGLE_USER WITH ROLLBACK IMMEDIATE evicts any lingering EF connection so
        // DROP DATABASE doesn't fail with "database in use".
        cmd.CommandText = $"""
            IF DB_ID('{SoakDatabaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{SoakDatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{SoakDatabaseName}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    private SqlConnection CreateMasterConnection()
    {
        var builder = new SqlConnectionStringBuilder(_containerConnectionString!)
        {
            InitialCatalog = "master"
        };
        return new SqlConnection(builder.ConnectionString);
    }

    private static string ResolveRepositoryRoot()
    {
        var dir = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))
                || File.Exists(Path.Combine(dir.FullName, "MillWorks.AuditCore.sln")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "Could not locate repository root (looked for .git or MillWorks.AuditCore.sln above TestDirectory).");
    }

    private static bool IsDockerUnavailable(Exception ex)
    {
        var message = ex.ToString();
        return message.Contains("Docker", StringComparison.OrdinalIgnoreCase)
               && (message.Contains("not", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("unable", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("connect", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("daemon", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("pipe", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("socket", StringComparison.OrdinalIgnoreCase));
    }

    private sealed record MemorySample(string Phase, string Label, TimeSpan Elapsed, long Bytes);
}
