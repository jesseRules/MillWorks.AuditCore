using Microsoft.Extensions.Options;

namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Archival configuration options
/// </summary>
public sealed class ArchivalOptions
{
    /// <summary>
    /// Archival storage provider
    /// </summary>
    public ArchivalProvider Provider { get; set; } = ArchivalProvider.FileSystem;

    /// <summary>
    /// Connection string for cloud storage (Azure Blob, AWS S3)
    /// </summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Retention days before archival
    /// </summary>
    public int RetentionDays { get; set; } = 365;

    /// <summary>
    /// Enable background archival process
    /// </summary>
    public bool EnableBackgroundArchival { get; set; } = false;

    /// <summary>
    /// Archival interval in hours
    /// </summary>
    public int ArchivalIntervalHours { get; set; } = 24;

    /// <summary>
    /// Interval in hours between archive integrity verification passes.
    /// Default: 24.
    /// </summary>
    public int VerificationIntervalHours { get; set; } = 24;

    /// <summary>
    /// Container/bucket name for cloud storage
    /// </summary>
    public string ContainerName { get; set; } = "audit-archives";

    /// <summary>
    /// Startup delay in seconds before the first archival cycle runs.
    /// Allows the application to fully initialize before background work begins.
    /// Default: 120 (2 minutes).
    /// </summary>
    public int StartupDelaySeconds { get; set; } = 120;
}

/// <summary>
/// Runtime validator for <see cref="ArchivalOptions"/>. Registered via the options pipeline
/// with <c>ValidateOnStart()</c> so misconfiguration fails at host boot, not at first archive.
/// </summary>
internal sealed class ArchivalOptionsValidator : IValidateOptions<ArchivalOptions>
{
    public ValidateOptionsResult Validate(string? name, ArchivalOptions options)
    {
        var failures = new List<string>();

        if (options.Provider == ArchivalProvider.AzureBlob
            && string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            failures.Add(
                $"{nameof(ArchivalOptions.ConnectionString)} is required when " +
                $"{nameof(ArchivalOptions.Provider)} is {nameof(ArchivalProvider.AzureBlob)}.");
        }

        if (options.Provider == ArchivalProvider.AWSs3)
        {
            failures.Add("AWS S3 archival provider is not yet implemented.");
        }

        if (options.RetentionDays <= 0)
        {
            failures.Add(
                $"{nameof(ArchivalOptions.RetentionDays)} must be > 0.");
        }

        if (options.ArchivalIntervalHours <= 0)
        {
            failures.Add(
                $"{nameof(ArchivalOptions.ArchivalIntervalHours)} must be > 0.");
        }

        if (options.VerificationIntervalHours <= 0)
        {
            failures.Add(
                $"{nameof(ArchivalOptions.VerificationIntervalHours)} must be > 0.");
        }

        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            failures.Add(
                $"{nameof(ArchivalOptions.ContainerName)} must not be empty.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}