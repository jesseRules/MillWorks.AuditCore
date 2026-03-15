namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Enumeration of possible archive statuses.
/// </summary>
public enum MillWorksArchiveStatus
{
    /// <summary>
    /// Archive creation is in progress.
    /// </summary>
    InProgress,
    
    /// <summary>
    /// Archive has been successfully created.
    /// </summary>
    Completed,
    
    /// <summary>
    /// Archive creation failed.
    /// </summary>
    Failed,
    
    /// <summary>
    /// Archive has been detected as corrupted.
    /// </summary>
    Corrupted
}