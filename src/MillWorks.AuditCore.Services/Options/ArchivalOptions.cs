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
}