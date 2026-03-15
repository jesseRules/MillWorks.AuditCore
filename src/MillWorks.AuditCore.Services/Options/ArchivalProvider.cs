namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Archival storage providers
/// </summary>
public enum ArchivalProvider
{
    /// <summary>
    /// Azure Blob Storage
    /// </summary>
    AzureBlob,

    /// <summary>
    /// AWS S3
    /// </summary>
    AWSs3,

    /// <summary>
    /// File System
    /// </summary>
    FileSystem
}