using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Interceptors;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Integration tests for AuditSaveChangesInterceptor with real SQLite database.
/// Validates the interceptor pipeline that mock-based tests can't fully exercise.
/// </summary>
[TestFixture]
[Category("Integration")]
public class AuditInterceptorIntegrationTests : IDisposable
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<AuditApplicationDbContext> _options;

    [SetUp]
    public void Setup()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(
                NullLogger<AuditSaveChangesInterceptor>.Instance))
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        using var context = new AuditApplicationDbContext(_options);
        context.Database.EnsureCreated();
    }

    [TearDown]
    public void TearDown()
    {
        _connection.Close();
        _connection.Dispose();
    }

    [Test]
    public async Task SavingAuditEntity_DoesNotCreateAuditLog()
    {
        // Arrange — saving an AuditEventEntity (an audit entity) should NOT create audit logs
        await using var context = new AuditApplicationDbContext(_options);

        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Test.Event",
            User = "tester",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Act
        await context.SaveChangesAsync();

        // Assert — no AuditLogEntity records should have been created
        var logCount = await context.AuditLogs.CountAsync();
        Assert.That(logCount, Is.EqualTo(0), "Saving an audit entity should not create audit log entries");
    }

    [Test]
    public async Task SavingMultipleAuditEntities_NoInfiniteLoop()
    {
        // This test validates that the bypass mechanism prevents infinite recursion:
        // Save AuditEvent -> interceptor fires -> checks bypass -> skips audit -> no infinite loop
        await using var context = new AuditApplicationDbContext(_options);

        for (int i = 0; i < 10; i++)
        {
            context.AuditEvents.Add(new AuditEventEntity
            {
                EventId = Guid.NewGuid(),
                EventType = $"Batch.Event{i}",
                InsertedDate = DateTimeOffset.UtcNow
            });
        }

        // Act — should complete without StackOverflowException
        var savedCount = await context.SaveChangesAsync();

        // Assert
        Assert.That(savedCount, Is.GreaterThan(0));
        var eventCount = await context.AuditEvents.CountAsync();
        Assert.That(eventCount, Is.EqualTo(10));
    }

    [Test]
    public async Task BypassFlag_PreventsInterceptorFromFiring()
    {
        // Arrange
        await using var context = new AuditApplicationDbContext(_options);

        // Manually set bypass flag
        context.BypassAuditInterceptor = true;

        context.AuditEvents.Add(new AuditEventEntity
        {
            EventId = Guid.NewGuid(),
            EventType = "Bypass.Test",
            InsertedDate = DateTimeOffset.UtcNow
        });

        // Act
        await context.SaveChangesAsync();

        // Assert — bypass should prevent audit log creation (same as normal for audit entities,
        // but validates the bypass path is exercised)
        context.BypassAuditInterceptor = false;
        var logCount = await context.AuditLogs.CountAsync();
        Assert.That(logCount, Is.EqualTo(0));
    }

    public void Dispose()
    {
        _connection?.Dispose();
    }
}
