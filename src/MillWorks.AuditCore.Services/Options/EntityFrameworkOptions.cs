using Microsoft.Extensions.Options;

namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Entity Framework options for audit logging with enhanced migration control
/// </summary>
public sealed class EntityFrameworkOptions
{
    /// <summary>
    /// Connection string for the audit database
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Migrate database schema on application startup
    /// </summary>
    public bool MigrateOnStartup { get; set; } = false;

    /// <summary>
    /// Automatically create database if it doesn't exist
    /// </summary>
    public bool EnsureDatabaseCreated { get; set; } = false;

    /// <summary>
    /// Seed initial data after migrations
    /// </summary>
    public bool SeedInitialData { get; set; }

    /// <summary>
    /// Fail application startup if migration fails
    /// </summary>
    public bool FailOnMigrationError { get; set; }

    /// <summary>
    /// Schema name for audit tables
    /// </summary>
    public string Schema { get; set; } = "audit";

    /// <summary>
    /// Timeout for migration operations in seconds
    /// </summary>
    public int MigrationTimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Runtime validator for <see cref="EntityFrameworkOptions"/>. Registered via the options
/// pipeline with <c>ValidateOnStart()</c> so misconfiguration fails at host boot, not at
/// first database access.
/// </summary>
internal sealed class EntityFrameworkOptionsValidator : IValidateOptions<EntityFrameworkOptions>
{
    public ValidateOptionsResult Validate(string? name, EntityFrameworkOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add(
                $"{nameof(EntityFrameworkOptions.ConnectionString)} is required. " +
                "Set ConnectionString in UseEntityFramework options.");
        }

        if (options.MigrationTimeoutSeconds <= 0)
        {
            failures.Add(
                $"{nameof(EntityFrameworkOptions.MigrationTimeoutSeconds)} must be > 0.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}