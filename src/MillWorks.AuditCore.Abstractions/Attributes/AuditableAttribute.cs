namespace MillWorks.AuditCore.Abstractions.Attributes;

/// <summary>
/// Attribute to mark classes or properties as auditable.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class AuditableAttribute : Attribute
{
    /// <summary>
    /// Indicates whether all properties of the class should be included in the audit log.
    /// </summary>
    public bool IncludeAllProperties { get; set; } = true;

    /// <summary>
    /// Indicates whether the class should be audited.
    /// </summary>
    public string[]? IncludeProperties { get; set; }

    /// <summary>
    /// Excludes specific properties from being audited.
    /// </summary>
    public string[]? ExcludeProperties { get; set; }

    /// <summary>
    /// Allows auditing of read operations on the entity.
    /// </summary>
    public bool AuditReads { get; set; } = true;
}
