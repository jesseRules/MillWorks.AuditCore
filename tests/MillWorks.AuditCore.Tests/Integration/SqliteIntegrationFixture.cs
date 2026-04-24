using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.Tests.Integration;

/// <summary>
/// Base fixture for SQLite-backed integration tests.
/// Uses a shared in-memory SQLite connection that persists for the test lifetime.
/// Clears all tables between tests to prevent data leakage.
/// </summary>
public abstract class SqliteIntegrationFixture : IDisposable
{
    private readonly SqliteConnection _connection;
    protected DbContextOptions<AuditApplicationDbContext> Options { get; }

    /// <summary>
    /// The shared in-memory SQLite connection backing every context created from
    /// <see cref="Options"/>. Exposed so tests that wire up their own DI container can
    /// register a DbContext pointing at the same in-memory database without reaching
    /// into EF's internal options APIs.
    /// </summary>
    protected SqliteConnection Connection => _connection;

    protected SqliteIntegrationFixture()
    {
        // In-memory SQLite requires the connection to stay open
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        Options = new DbContextOptionsBuilder<AuditApplicationDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(static w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning))
            .Options;

        // Create schema from model (not migrations)
        using var context = CreateContext();
        context.Database.EnsureCreated();
    }

    protected AuditApplicationDbContext CreateContext()
    {
        return new AuditApplicationDbContext(Options);
    }

    /// <summary>
    /// Clears all data between tests to prevent leakage.
    /// </summary>
    [TearDown]
    public void CleanupData()
    {
        using var context = CreateContext();
        // Delete child tables first to respect Restrict FK constraints
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditIntegrity\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditIntegrityWorkItems\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"SecurityEvents\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditEvents\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"AuditLogs\"");
        context.Database.ExecuteSqlRaw("DELETE FROM \"ArchiveRecord\"");
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}
