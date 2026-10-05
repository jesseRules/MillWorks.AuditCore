using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Query;

namespace MillWorks.AuditCore.Services;

/// <summary>Indexed subject lookup across AuditCore's active database evidence families.</summary>
public sealed class AuditSubjectLookupService(AuditDbContext context) : IAuditSubjectLookupService
{
    /// <inheritdoc />
    public async Task<AuditSubjectLookupPage> FindAsync(
        Guid tenantId,
        Guid subjectId,
        AuditSubjectLookupCursor? cursor = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("Tenant identity must be non-empty.", nameof(tenantId));
        if (subjectId == Guid.Empty)
            throw new ArgumentException("Subject identity must be non-empty.", nameof(subjectId));

        limit = QueryLimits.Clamp(limit);
        string subjectText = subjectId.ToString();
        DateTimeOffset cursorTime = cursor?.OccurredAt ?? DateTimeOffset.MinValue;
        int cursorStore = (int?)cursor?.Store ?? 0;
        Guid cursorId = cursor?.RecordId ?? Guid.Empty;
        bool hasCursor = cursor is not null;

        const int eventStore = (int)AuditSubjectRecordStore.AuditEvent;
        const int logStore = (int)AuditSubjectRecordStore.AuditLog;
        const int securityStore = (int)AuditSubjectRecordStore.SecurityEvent;

        var eventRows = await context.AuditEvents.AsNoTracking()
            .Where(row => row.TenantId == tenantId
                          && (row.UserId == subjectId || row.AspNetUserId == subjectText)
                          && (!hasCursor
                              || (row.InsertedDate ?? row.CreatedAt) > cursorTime
                              || ((row.InsertedDate ?? row.CreatedAt) == cursorTime && eventStore > cursorStore)
                              || ((row.InsertedDate ?? row.CreatedAt) == cursorTime
                                  && eventStore == cursorStore
                                  && row.EventId.CompareTo(cursorId) > 0)))
            .OrderBy(static row => row.InsertedDate ?? row.CreatedAt)
            .ThenBy(static row => row.EventId)
            .Select(static row => new { RecordId = row.EventId, OccurredAt = row.InsertedDate ?? row.CreatedAt })
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        SubjectCandidate[] events = eventRows
            .Select(static row => new SubjectCandidate(
                AuditSubjectRecordStore.AuditEvent,
                row.RecordId,
                row.OccurredAt))
            .ToArray();

        var logRows = await context.AuditLogSubjectLinks.AsNoTracking()
            .Where(link => link.TenantId == tenantId
                           && link.SubjectId == subjectId
                           && (!hasCursor
                               || link.OccurredAt > cursorTime
                               || (link.OccurredAt == cursorTime && logStore > cursorStore)
                               || (link.OccurredAt == cursorTime
                                   && logStore == cursorStore
                                   && link.AuditLogId.CompareTo(cursorId) > 0)))
            .Select(static link => new { RecordId = link.AuditLogId, link.OccurredAt })
            .Distinct()
            .OrderBy(static row => row.OccurredAt)
            .ThenBy(static row => row.RecordId)
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        SubjectCandidate[] logs = logRows
            .Select(static row => new SubjectCandidate(
                AuditSubjectRecordStore.AuditLog,
                row.RecordId,
                row.OccurredAt))
            .ToArray();

        var securityRows = await context.SecurityEvents.AsNoTracking()
            .Where(row => row.TenantId == tenantId
                          && (row.ActorUserId == subjectId || row.SubjectUserId == subjectId)
                          && (!hasCursor
                              || row.DetectedAt > cursorTime
                              || (row.DetectedAt == cursorTime && securityStore > cursorStore)
                              || (row.DetectedAt == cursorTime
                                  && securityStore == cursorStore
                                  && row.Id.CompareTo(cursorId) > 0)))
            .OrderBy(static row => row.DetectedAt)
            .ThenBy(static row => row.Id)
            .Select(static row => new { RecordId = row.Id, OccurredAt = row.DetectedAt })
            .Take(limit + 1)
            .ToArrayAsync(cancellationToken);
        SubjectCandidate[] securityEvents = securityRows
            .Select(static row => new SubjectCandidate(
                AuditSubjectRecordStore.SecurityEvent,
                row.RecordId,
                row.OccurredAt))
            .ToArray();

        SubjectCandidate[] candidates = events
            .Concat(logs)
            .Concat(securityEvents)
            .OrderBy(static row => row.OccurredAt)
            .ThenBy(static row => row.Store)
            .ThenBy(static row => row.RecordId)
            .Take(limit + 1)
            .ToArray();

        bool hasMore = candidates.Length > limit;
        SubjectCandidate[] pageCandidates = hasMore ? candidates[..limit] : candidates;
        AuditSubjectRecordReference[] items = pageCandidates
            .Select(static row => new AuditSubjectRecordReference(row.Store, row.RecordId, row.OccurredAt))
            .ToArray();
        AuditSubjectLookupCursor? next = hasMore && pageCandidates.Length != 0
            ? new AuditSubjectLookupCursor(
                pageCandidates[^1].OccurredAt,
                pageCandidates[^1].Store,
                pageCandidates[^1].RecordId)
            : null;

        return new AuditSubjectLookupPage(items, next);
    }

    private sealed record SubjectCandidate(
        AuditSubjectRecordStore Store,
        Guid RecordId,
        DateTimeOffset OccurredAt);
}
