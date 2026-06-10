using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace MillWorks.AuditCore.EntityFramework.Options;

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
    /// Timeout for migration operations in seconds. Only applies during database
    /// migrations, not runtime queries.
    /// </summary>
    public int MigrationTimeoutSeconds { get; set; } = 300;

    /// <summary>
    /// Command timeout for runtime queries in seconds. Defaults to 30 seconds.
    /// This is distinct from <see cref="MigrationTimeoutSeconds"/> which only applies
    /// during database migrations.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Runtime validator for <see cref="EntityFrameworkOptions"/>. Registered via the options
/// pipeline with <c>ValidateOnStart()</c> so misconfiguration fails at host boot, not at
/// first database access.
/// </summary>
internal sealed class EntityFrameworkOptionsValidator : IValidateOptions<EntityFrameworkOptions>
{
    private static readonly Regex SchemaIdentifierRegex =
        new(@"^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    private static readonly HashSet<string> ReservedSchemas =
        new(StringComparer.OrdinalIgnoreCase) { "dbo", "sys", "guest", "INFORMATION_SCHEMA" };

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

        if (options.CommandTimeoutSeconds <= 0)
        {
            failures.Add(
                $"{nameof(EntityFrameworkOptions.CommandTimeoutSeconds)} must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(options.Schema))
        {
            failures.Add($"{nameof(EntityFrameworkOptions.Schema)} must not be null or whitespace.");
        }
        else if (!SchemaIdentifierRegex.IsMatch(options.Schema))
        {
            failures.Add($"{nameof(EntityFrameworkOptions.Schema)} must match identifier pattern ^[A-Za-z_][A-Za-z0-9_]{{0,127}}$.");
        }
        else if (ReservedSchemas.Contains(options.Schema))
        {
            failures.Add($"{nameof(EntityFrameworkOptions.Schema)} must not be a reserved SQL Server schema (dbo, sys, guest, INFORMATION_SCHEMA).");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}