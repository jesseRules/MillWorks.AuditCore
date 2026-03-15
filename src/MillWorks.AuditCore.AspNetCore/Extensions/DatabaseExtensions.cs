using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;

namespace MillWorks.AuditCore.AspNetCore.Extensions;

/// <summary>
/// Extension methods for easy database initialization (SDK-compatible, no web dependencies)
/// </summary>
public static class DatabaseExtensions
{
    /// <param name="serviceProvider">Service provider</param>
    extension(IServiceProvider serviceProvider)
    {
        /// <summary>
        /// Runs database migrations for the audit system.
        /// Migrate() creates the database if it doesn't exist AND applies migrations.
        /// </summary>
        public void RunAuditMigrations()
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();
            var logger = scope.ServiceProvider.GetService<ILogger<AuditApplicationDbContext>>();

            try
            {
                logger?.LogInformation("Starting MillWorks.Audit database migration...");

                var pendingMigrations = context.Database.GetPendingMigrations();
                IEnumerable<string> migrations = pendingMigrations as string[] ?? pendingMigrations.ToArray();
                if (migrations.Any())
                {
                    logger?.LogInformation("Found {Count} pending migrations", migrations.Count());
                    context.Database.Migrate();
                    logger?.LogInformation("Migrations applied successfully");
                }
                else
                {
                    logger?.LogInformation("No pending migrations found");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to run database migrations");
                throw;
            }
        }

        /// <summary>
        /// Runs database migrations asynchronously for the audit system.
        /// MigrateAsync() creates the database if it doesn't exist AND applies migrations.
        /// </summary>
        public async Task RunAuditMigrationsAsync()
        {
            using var scope = serviceProvider.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<AuditApplicationDbContext>();
            var logger = scope.ServiceProvider.GetService<ILogger<AuditApplicationDbContext>>();

            try
            {
                logger?.LogInformation("Starting MillWorks.Audit database migration (async)...");

                var pendingMigrations = await context.Database.GetPendingMigrationsAsync();
                IEnumerable<string> migrations = pendingMigrations as string[] ?? pendingMigrations.ToArray();
                if (migrations.Any())
                {
                    logger?.LogInformation("Found {Count} pending migrations", migrations.Count());
                    await context.Database.MigrateAsync();
                    logger?.LogInformation("Migrations applied successfully");
                }
                else
                {
                    logger?.LogInformation("No pending migrations found");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to run database migrations");
                throw;
            }
        }
    }


}