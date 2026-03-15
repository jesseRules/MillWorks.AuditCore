using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Database;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Tests for DatabaseInitializer static methods
/// Note: These tests expect exceptions because in-memory database doesn't support migrations
/// </summary>
[TestFixture]
public class DatabaseInitializerInMemoryTests
{
    /// <summary>
    /// Service provider for dependency injection
    /// </summary>
    private ServiceProvider _serviceProvider;

    /// <summary>
    /// Context for the in-memory database
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Setup method to initialize in-memory database and service provider
    /// </summary>
    [SetUp]
    public void Setup()
    {
        var services = new ServiceCollection();

        var dbOptions = TestDbContextFactory.CreateInMemoryOptions();

        _context = new AuditApplicationDbContext(dbOptions);

        services.AddSingleton(_context);
        services.AddSingleton(_context);
        services.AddLogging(static builder => builder.AddConsole());

        _serviceProvider = services.BuildServiceProvider();
    }

    /// <summary>
    /// Tear down method to dispose resources
    /// </summary>
    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
        _serviceProvider.Dispose();
    }

    /// <summary>
    /// DatabaseInitializer.InitializeAsync throws InvalidOperationException with in-memory database
    /// </summary>
    [Test]
    public void DatabaseInitializer_InitializeAsync_ThrowsWithInMemoryDatabase()
    {
        // Act & Assert - In-memory database doesn't support migrations
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DatabaseInitializer.InitializeAsync(_serviceProvider));
    }

    /// <summary>
    /// DatabaseInitializer.NeedsMigrationAsync throws InvalidOperationException with in-memory database
    /// </summary>
    [Test]
    public void DatabaseInitializer_NeedsMigrationAsync_ThrowsWithInMemoryDatabase()
    {
        // Act & Assert - In-memory database doesn't support migrations
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DatabaseInitializer.NeedsMigrationAsync(_serviceProvider));
    }

    /// <summary>
    /// DatabaseInitializer.GetPendingMigrationsAsync throws InvalidOperationException with in-memory database
    /// </summary>
    [Test]
    public void DatabaseInitializer_GetPendingMigrationsAsync_ThrowsWithInMemoryDatabase()
    {
        // Act & Assert - In-memory database doesn't support migrations
        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await DatabaseInitializer.GetPendingMigrationsAsync(_serviceProvider));
    }
}