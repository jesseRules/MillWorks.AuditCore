namespace MillWorks.AuditCore.Abstractions.Enums;

/// <summary>AuditCore database family containing a subject-linked record.</summary>
public enum AuditSubjectRecordStore
{
    /// <summary>Explicit audit event.</summary>
    AuditEvent = 1,
    /// <summary>Entity-change audit log.</summary>
    AuditLog = 2,
    /// <summary>Security event.</summary>
    SecurityEvent = 3
}
