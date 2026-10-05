using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>Stable keyset cursor for a tenant-scoped subject lookup.</summary>
public sealed record AuditSubjectLookupCursor(
    DateTimeOffset OccurredAt,
    AuditSubjectRecordStore Store,
    Guid RecordId);

/// <summary>Location of one AuditCore record linked to the requested subject.</summary>
public sealed record AuditSubjectRecordReference(
    AuditSubjectRecordStore Store,
    Guid RecordId,
    DateTimeOffset OccurredAt);

/// <summary>One bounded page of subject-linked AuditCore record locations.</summary>
public sealed record AuditSubjectLookupPage(
    IReadOnlyList<AuditSubjectRecordReference> Items,
    AuditSubjectLookupCursor? NextCursor);
