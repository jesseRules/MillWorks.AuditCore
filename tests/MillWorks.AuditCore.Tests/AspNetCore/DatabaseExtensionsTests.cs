using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.AspNetCore.Extensions;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.Tests.AspNetCore;

[TestFixture]
[Category("Integration")]
public sealed class DatabaseExtensionsTests
{
    private string? _dbPath;

    [TearDown]
    public void TearDown()
    {
        if (!string.IsNullOrWhiteSpace(_dbPath) && File.Exists(_dbPath))
            File.Delete(_dbPath);
    }

    [Test]
    public void RunAuditMigrationsAsync_WithSqliteProvider_SurfacesMigrationFailure()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"auditcore-migrate-{Guid.NewGuid():N}.db");
        var logger = new Mock<ILogger<AuditDbContext>>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(logger.Object);
        services.AddDbContext<AuditDbContext>(options =>
            options.UseSqlite($"Data Source={_dbPath}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning)));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.ThrowsAsync<SqliteException>(async () => await provider.RunAuditMigrationsAsync());

        Assert.That(ex!.Message, Does.Contain("syntax error"));
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to run database migrations")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    [Test]
    public void RunAuditMigrations_WithInMemoryProvider_SurfacesError()
    {
        var logger = new Mock<ILogger<AuditDbContext>>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(logger.Object);
        services.AddDbContext<AuditDbContext>(options =>
            options.UseInMemoryDatabase($"db-{Guid.NewGuid():N}"));

        using var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<InvalidOperationException>(() => provider.RunAuditMigrations());

        Assert.That(ex!.Message, Does.Contain("Relational-specific methods"));
        logger.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) => v.ToString()!.Contains("Failed to run database migrations")),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }
}
