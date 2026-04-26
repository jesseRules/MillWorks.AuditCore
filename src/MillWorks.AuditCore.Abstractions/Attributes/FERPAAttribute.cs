namespace MillWorks.AuditCore.Abstractions.Attributes;

/// <summary>
/// Marks a class as containing FERPA-protected educational records.
/// When applied, the AuditSaveChangesInterceptor generates FERPA-prefixed event types
/// (e.g., "FERPA.Student.Updated") and includes consent metadata in audit logs.
/// Note: The interceptor hooks SaveChanges — it audits write operations only.
/// Read auditing requires a separate mechanism (middleware or query interceptor).
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class FERPAAttribute : Attribute
{
    /// <summary>
    /// Type of educational record
    /// </summary>
    public string RecordType { get; set; } = "StudentRecord";

    /// <summary>
    /// Whether consent is required for data sharing
    /// </summary>
    public bool RequiresConsent { get; set; } = true;

    /// <summary>
    /// Whether to log all access to this data
    /// </summary>
    public bool LogAllAccess { get; set; } = true;
}
