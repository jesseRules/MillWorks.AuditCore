namespace MillWorks.AuditCore.Abstractions.Dto;

/// <summary>
/// Tamper severity levels
/// </summary>
public enum TamperSeverity
{
    /// <summary>
    /// Low severity indicates a minor tampering event that does not significantly impact data integrity
    /// </summary>
    Low,
     
    /// <summary>
    /// Medium severity indicates a moderate tampering event that may require investigation
    /// </summary>
    Medium,
    
    /// <summary>
    /// High severity indicates a significant tampering event that could impact data integrity
    /// </summary>
    High,
    
    /// <summary>
    /// Critical severity indicates a severe tampering event that requires immediate attention
    /// </summary>
    Critical
}