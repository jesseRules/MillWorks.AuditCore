using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Options;

namespace MillWorks.AuditCore.Services.Database;

/// <summary>
/// Hosted service that handles database initialization including migrations and seeding
/// </summary>
public sealed class DatabaseInitializationService : IHostedService
{
    /// <summary>
    /// Service provider for creating scopes
    /// </summary>
    private readonly IServiceProvider _serviceProvider;

    /// <summary>
    /// Logger for database initialization
    /// </summary>
    private readonly ILogger<DatabaseInitializationService> _logger;

    /// <summary>
    /// Options for Entity Framework database initialization
    /// </summary>
    private readonly IOptions<EntityFrameworkOptions> _options;

    /// <summary>
    /// Creates a new instance of the database initialization service
    /// </summary>
    public DatabaseInitializationService(
        IServiceProvider serviceProvider,
        ILogger<DatabaseInitializationService> logger,
        IOptions<EntityFrameworkOptions> options)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _serviceProvider = serviceProvider;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Starts the service and runs migrations if configured
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var options = _options.Value;

        if (!options.MigrateOnStartup && !options.EnsureDatabaseCreated)
        {
            _logger.LogInformation("Database migration on startup is disabled");
            return;
        }

        _logger.LogInformation("Starting database initialization for MillWorks.Audit");

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            if (options.MigrateOnStartup)
            {
                // MigrateAsync creates the database if it doesn't exist AND applies migrations.
                // Do NOT call EnsureCreated here — it creates schema without __EFMigrationsHistory
                // which causes Migrate to fail on subsequent runs.
                var pendingMigrations = await context.Database.GetPendingMigrationsAsync(cancellationToken);
                IEnumerable<string> migrations = pendingMigrations as string[] ?? pendingMigrations.ToArray();
                if (migrations.Any())
                {
                    _logger.LogInformation("Found {Count} pending migrations. Applying migrations...",
                        migrations.Count());

                    foreach (var migration in migrations)
                    {
                        _logger.LogDebug("Pending migration: {Migration}", migration);
                    }

                    await context.Database.MigrateAsync(cancellationToken);
                    _logger.LogInformation("Database migrations completed successfully");
                }
                else
                {
                    _logger.LogInformation("Database is up to date. No migrations needed");
                }
            }
            else if (options.EnsureDatabaseCreated)
            {
                // EnsureCreated is for prototyping/testing only — no migration history is tracked.
                // Do NOT combine with MigrateOnStartup.
                await context.Database.EnsureCreatedAsync(cancellationToken);
                _logger.LogInformation("Database schema created via EnsureCreated (no migration history)");
            }

            // Optional: Seed initial data if needed
            if (options.SeedInitialData)
            {
                await SeedInitialDataAsync(context, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during database initialization");

            // Only throw if we're configured to fail on migration errors
            if (options.FailOnMigrationError)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Seeds initial data if needed
    /// </summary>
    private async Task SeedInitialDataAsync(AuditDbContext context, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Checking for initial data seeding requirements...");

            // Add any initial data seeding logic here
            // For example, creating system audit events or initial configuration

            await context.SaveChangesAsync(cancellationToken);
            _logger.LogInformation("Initial data seeding completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred during data seeding");
        }
    }

    /// <summary>
    /// Stops the service
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Database initialization service stopped");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Static helper class for easy database initialization
/// </summary>
public class DatabaseInitializer
{
    /// <summary>
    /// Manually initializes the audit database with migrations
    /// </summary>
    public static async Task InitializeAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<DatabaseInitializer>>();

        try
        {
            logger.LogInformation("Manually initializing MillWorks.Audit database...");

            var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

            // MigrateAsync creates the database if needed AND applies migrations.
            // Do NOT call EnsureCreated — it creates schema without __EFMigrationsHistory
            // which causes Migrate to fail on subsequent runs.
            await context.Database.MigrateAsync();

            logger.LogInformation("Database initialization completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to initialize database");
            throw;
        }
    }

    /// <summary>
    /// Checks if the database needs migration
    /// </summary>
    public static async Task<bool> NeedsMigrationAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
        return pendingMigrations.Any();
    }

    /// <summary>
    /// Gets the list of pending migrations
    /// </summary>
    public static async Task<IEnumerable<string>> GetPendingMigrationsAsync(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuditDbContext>();

        return await context.Database.GetPendingMigrationsAsync();
    }
}