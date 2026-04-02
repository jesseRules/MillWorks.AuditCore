using System.Diagnostics;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;

namespace MillWorks.AuditCore.Tests.Repositories;

/// <summary>
/// Phase 5: Performance tests for core repository operations.
/// Establishes baselines for query and insert performance using SQLite.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class RepositoryPerformanceTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private AuditApplicationDbContext _dbContext = null!;

    [SetUp]
    public void SetUp()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .Options;
        _dbContext = new AuditApplicationDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _dbContext.Dispose();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    // ── Batch insert throughput ──

    [Test]
    public async Task AddRange_100Events_MeasureThroughput()
    {
        var repo = new AuditEventRepository(_dbContext);
        var events = CreateEvents(100);

        var sw = Stopwatch.StartNew();
        foreach (var evt in events)
            await repo.AddAsync(evt);
        await _dbContext.SaveChangesAsync();
        sw.Stop();

        TestContext.Out.WriteLine($"Insert 100 events: {sw.ElapsedMilliseconds}ms ({100_000.0 / sw.ElapsedMilliseconds:F0} events/sec)");
        sw.ElapsedMilliseconds.Should().BeLessThan(10_000, "inserting 100 events should be fast");
    }

    [Test]
    public async Task AddRange_1000Events_MeasureThroughput()
    {
        var repo = new AuditEventRepository(_dbContext);
        var events = CreateEvents(1000);

        var sw = Stopwatch.StartNew();
        foreach (var evt in events)
            await repo.AddAsync(evt);
        await _dbContext.SaveChangesAsync();
        sw.Stop();

        var eventsPerSecond = 1000.0 / sw.Elapsed.TotalSeconds;
        TestContext.Out.WriteLine($"Insert 1000 events: {sw.ElapsedMilliseconds}ms ({eventsPerSecond:F0} events/sec)");
        eventsPerSecond.Should().BeGreaterThan(100, "should insert at least 100 events/sec");
    }

    // ── GetById latency with varying table sizes ──

    [Test]
    public async Task GetById_With1KRows_MeasureLatency()
    {
        await SeedEvents(1000);
        var repo = new AuditEventRepository(_dbContext);

        // Pick a known event to query
        var targetEvent = await _dbContext.AuditEvents.FirstAsync();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var result = await repo.GetByIdAsync(targetEvent.EventId);
            result.Should().NotBeNull();
        }
        sw.Stop();

        var avgLatencyMs = sw.ElapsedMilliseconds / 100.0;
        TestContext.Out.WriteLine($"GetById (1K rows): avg {avgLatencyMs:F2}ms over 100 queries");
        avgLatencyMs.Should().BeLessThan(50, "single GetById should be fast");
    }

    [Test]
    public async Task GetById_With10KRows_MeasureLatency()
    {
        await SeedEvents(10_000);
        var repo = new AuditEventRepository(_dbContext);

        var targetEvent = await _dbContext.AuditEvents.FirstAsync();

        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 100; i++)
        {
            var result = await repo.GetByIdAsync(targetEvent.EventId);
            result.Should().NotBeNull();
        }
        sw.Stop();

        var avgLatencyMs = sw.ElapsedMilliseconds / 100.0;
        TestContext.Out.WriteLine($"GetById (10K rows): avg {avgLatencyMs:F2}ms over 100 queries");
        avgLatencyMs.Should().BeLessThan(100, "GetById with 10K rows should still be fast");
    }

    // ── Filter queries ──

    [Test]
    public async Task GetByEventType_With10KRows_UsesIndex()
    {
        await SeedEvents(10_000);
        var repo = new AuditEventRepository(_dbContext);

        var sw = Stopwatch.StartNew();
        var results = await repo.GetByEventTypeAsync("Event.Type.0");
        sw.Stop();

        TestContext.Out.WriteLine($"GetByEventType (10K rows): {sw.ElapsedMilliseconds}ms, found {results.Count()} events");
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    [Test]
    public async Task GetByDateRange_With10KRows_MeasurePerformance()
    {
        await SeedEvents(10_000);
        var repo = new AuditEventRepository(_dbContext);

        var start = DateTimeOffset.UtcNow.AddDays(-5);
        var end = DateTimeOffset.UtcNow;

        var sw = Stopwatch.StartNew();
        var results = await repo.GetByDateRangeAsync(start, end);
        sw.Stop();

        TestContext.Out.WriteLine($"GetByDateRange (10K rows): {sw.ElapsedMilliseconds}ms, found {results.Count()} events");
        sw.ElapsedMilliseconds.Should().BeLessThan(5000);
    }

    // ── Concurrent read + write ──

    [Test]
    public async Task ConcurrentReadWrite_NoLockContention()
    {
        await SeedEvents(1000);
        var targetEvent = await _dbContext.AuditEvents.FirstAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var readTask = Task.Run(async () =>
        {
            var readOptions = new DbContextOptionsBuilder<AuditApplicationDbContext>()
                .UseSqlite(_connection)
                .Options;
            await using var readCtx = new AuditApplicationDbContext(readOptions);
            var repo = new AuditEventRepository(readCtx);
            for (var i = 0; i < 50 && !cts.Token.IsCancellationRequested; i++)
            {
                await repo.GetByIdAsync(targetEvent.EventId, cts.Token);
            }
        });

        var writeTask = Task.Run(async () =>
        {
            var writeOptions = new DbContextOptionsBuilder<AuditApplicationDbContext>()
                .UseSqlite(_connection)
                .Options;
            await using var writeCtx = new AuditApplicationDbContext(writeOptions);
            var repo = new AuditEventRepository(writeCtx);
            for (var i = 0; i < 20 && !cts.Token.IsCancellationRequested; i++)
            {
                await repo.AddAsync(new AuditEventEntity
                {
                    EventId = Guid.NewGuid(),
                    EventType = "Concurrent.Write",
                    InsertedDate = DateTimeOffset.UtcNow,
                    User = "writer"
                }, cts.Token);
                await writeCtx.SaveChangesAsync(cts.Token);
            }
        });

        var act = () => Task.WhenAll(readTask, writeTask);
        await act.Should().NotThrowAsync("concurrent read + write should not deadlock");
    }

    #region Helpers

    private async Task SeedEvents(int count)
    {
        var events = CreateEvents(count);
        _dbContext.AuditEvents.AddRange(events);
        await _dbContext.SaveChangesAsync();
        _dbContext.ChangeTracker.Clear();
    }

    private static List<AuditEventEntity> CreateEvents(int count)
    {
        return Enumerable.Range(0, count).Select(i => new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = $"Event.Type.{i % 10}",
            InsertedDate = DateTimeOffset.UtcNow.AddMinutes(-i),
            User = $"user{i % 50}",
            EntityType = $"Entity{i % 5}",
            Action = "Created",
            JsonData = $"{{\"index\":{i},\"data\":\"value_{i}\"}}"
        }).ToList();
    }

    #endregion
}
