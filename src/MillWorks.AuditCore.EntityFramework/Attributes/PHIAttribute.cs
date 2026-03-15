namespace MillWorks.AuditCore.EntityFramework.Attributes;

/// <summary>
/// Marks a class as containing PHI (Protected Health Information) for HIPAA compliance
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PHIAttribute : Attribute
{
    /// <summary>
    /// Type of PHI
    /// </summary>
    public string DataType { get; set; } = "HealthRecord";
    
    /// <summary>
    /// Whether this is a limited data set
    /// </summary>
    public bool IsLimitedDataSet { get; set; } = false;
    
    /// <summary>
    /// Whether to log all access for HIPAA audit trail
    /// </summary>
    public bool LogAllAccess { get; set; } = true;
}