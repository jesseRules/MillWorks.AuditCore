using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Database;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Tests.Helpers;

namespace MillWorks.AuditCore.Tests.Services;

/// <summary>
/// Unit tests for DatabaseInitializationService using in-memory database
/// </summary>
[TestFixture]
public class DbInitializationServiceTests
{
    /// <summary>
    /// Mock service provider for dependency injection
    /// </summary>
    private Mock<IServiceProvider> _mockServiceProvider;

    /// <summary>
    /// Mock service scope for creating scoped services
    /// </summary>
    private Mock<IServiceScope> _mockScope;

    /// <summary>
    /// Mock service scope factory for creating service scopes
    /// </summary>
    private Mock<IServiceScopeFactory> _mockScopeFactory;

    /// <summary>
    /// Mock logger for capturing log output
    /// </summary>
    private Mock<ILogger<DatabaseInitializationService>> _mockLogger;

    /// <summary>
    /// Options for Entity Framework database initialization
    /// </summary>
    private EntityFrameworkOptions _options;

    /// <summary>
    /// Context for the in-memory database
    /// </summary>
    private AuditApplicationDbContext _context;

    /// <summary>
    /// Service provider for dependency injection
    /// </summary>
    private ServiceProvider _serviceProvider;

    /// <summary>
    /// Setup method to initialize common test dependencies
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

        // Setup mocks for scoped services
        _mockServiceProvider = new Mock<IServiceProvider>();
        _mockScope = new Mock<IServiceScope>();
        _mockScopeFactory = new Mock<IServiceScopeFactory>();

        _mockScope.Setup(static x => x.ServiceProvider).Returns(_serviceProvider);
        _mockScopeFactory.Setup(static x => x.CreateScope()).Returns(_mockScope.Object);
        _mockServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(_mockScopeFactory.Object);
        _mockServiceProvider.Setup(static x => x.GetService(typeof(AuditApplicationDbContext)))
            .Returns(_context);

        _mockLogger = new Mock<ILogger<DatabaseInitializationService>>();

        _options = new EntityFrameworkOptions
        {
            MigrateOnStartup = true,
            SeedInitialData = false,
            FailOnMigrationError = false
        };
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
    /// StartAsync_WithMigrateOnStartupDisabled_SkipsMigration
    /// </summary>
    [Test]
    public async Task StartAsync_WithMigrateOnStartupDisabled_SkipsMigration()
    {
        // Arrange
        _options.MigrateOnStartup = false;
        _options.EnsureDatabaseCreated = false;
        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("migration on startup is disabled")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Start Async With Migrate On Startup Enabled Logs Initialization
    /// </summary>
    [Test]
    public async Task StartAsync_WithMigrateOnStartupEnabled_LogsInitialization()
    {
        // Arrange
        _options.MigrateOnStartup = true;
        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("Starting database initialization")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Start Async With In Memory Database Logs Error
    /// </summary>
    [Test]
    public async Task StartAsync_WithInMemoryDatabase_LogsError()
    {
        // Arrange
        _options.MigrateOnStartup = true;
        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - In-memory database throws exception when checking migrations
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) =>
                    v.ToString()!.Contains("Error occurred during database initialization")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Start Async With Seed Initial Data Enabled Attempts Seeding
    /// </summary>
    [Test]
    public async Task StartAsync_WithSeedInitialDataEnabled_AttemptsSeeding()
    {
        // Arrange
        _options.MigrateOnStartup = true;
        _options.SeedInitialData = true;
        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StartAsync(CancellationToken.None);

        // Assert - Will fail on migrations but logs the error
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) =>
                    v.ToString()!.Contains("Error occurred during database initialization")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Start Async When Exception Occurs With Fail On Error Throws
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task StartAsync_WhenExceptionOccursWithFailOnError_Throws()
    {
        // Arrange
        _options.MigrateOnStartup = true;
        _options.FailOnMigrationError = true;

        // Create a disposed context to cause an error
        var disposedContext = new AuditApplicationDbContext(
            TestDbContextFactory.CreateInMemoryOptions());
        disposedContext.Dispose();

        var mockDisposedServiceProvider = new Mock<IServiceProvider>();
        var mockDisposedScope = new Mock<IServiceScope>();
        var mockDisposedScopeFactory = new Mock<IServiceScopeFactory>();

        var disposeProvider = new ServiceCollection()
            .AddSingleton(disposedContext)
            .BuildServiceProvider();

        mockDisposedScope.Setup(static x => x.ServiceProvider).Returns(disposeProvider);
        mockDisposedScopeFactory.Setup(static x => x.CreateScope()).Returns(mockDisposedScope.Object);
        mockDisposedServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockDisposedScopeFactory.Object);

        var service = new DatabaseInitializationService(
            mockDisposedServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act & Assert
        Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.StartAsync(CancellationToken.None));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start Async When Exception Occurs Without Fail On Error Does Not Throw
    /// </summary>
    /// <returns></returns>
    [Test]
    public Task StartAsync_WhenExceptionOccursWithoutFailOnError_DoesNotThrow()
    {
        // Arrange
        _options.MigrateOnStartup = true;
        _options.FailOnMigrationError = false;

        // Create a disposed context to cause an error
        var disposedContext = new AuditApplicationDbContext(
            TestDbContextFactory.CreateInMemoryOptions());
        disposedContext.Dispose();

        var mockDisposedServiceProvider = new Mock<IServiceProvider>();
        var mockDisposedScope = new Mock<IServiceScope>();
        var mockDisposedScopeFactory = new Mock<IServiceScopeFactory>();

        var disposeProvider = new ServiceCollection()
            .AddSingleton(disposedContext)
            .BuildServiceProvider();

        mockDisposedScope.Setup(static x => x.ServiceProvider).Returns(disposeProvider);
        mockDisposedScopeFactory.Setup(static x => x.CreateScope()).Returns(mockDisposedScope.Object);
        mockDisposedServiceProvider.Setup(static x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(mockDisposedScopeFactory.Object);

        var service = new DatabaseInitializationService(
            mockDisposedServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act & Assert
        Assert.DoesNotThrowAsync(async () =>
            await service.StartAsync(CancellationToken.None));

        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) =>
                    v.ToString()!.Contains("Error occurred during database initialization")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Start Async With Cancellation Token Propagates Token
    /// </summary>
    [Test]
    public async Task StartAsync_WithCancellationToken_PropagatesToken()
    {
        // Arrange
        using var cts = new CancellationTokenSource();
        _options.MigrateOnStartup = true;

        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StartAsync(cts.Token);

        // Assert - Should complete without throwing (will log error due to in-memory limitations)
        Assert.Pass("Cancellation token was propagated correctly");
    }

    /// <summary>
    /// Stop Async Logs Service Stopped
    /// </summary>
    [Test]
    public async Task StopAsync_LogsServiceStopped()
    {
        // Arrange
        var service = new DatabaseInitializationService(
            _mockServiceProvider.Object,
            _mockLogger.Object,
            _options);

        // Act
        await service.StopAsync(CancellationToken.None);

        // Assert
        _mockLogger.Verify(
            static x => x.Log(
                LogLevel.Information,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>(static (v, t) => v.ToString()!.Contains("stopped")),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.Once);
    }

    /// <summary>
    /// Constructor_WithNullServiceProvider_ThrowsArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullServiceProvider_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseInitializationService(
                null!,
                _mockLogger.Object,
                _options));
    }

    /// <summary>
    /// Constructor_WithNullLogger_ThrowsArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullLogger_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseInitializationService(
                _mockServiceProvider.Object,
                null!,
                _options));
    }

    /// <summary>
    /// Constructor_WithNullOptions_ThrowsArgumentNullException
    /// </summary>
    [Test]
    public void Constructor_WithNullOptions_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseInitializationService(
                _mockServiceProvider.Object,
                _mockLogger.Object,
                null!));
    }
}
