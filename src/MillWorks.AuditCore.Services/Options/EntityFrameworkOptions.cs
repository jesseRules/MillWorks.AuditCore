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