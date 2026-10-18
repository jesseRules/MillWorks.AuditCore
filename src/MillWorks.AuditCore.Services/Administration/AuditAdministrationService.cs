using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;
using MillWorks.AuditCore.Services.Database.Options;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Administration;

/// <summary>Executes tenant-bound operations and writes payload-free, append-only action evidence.</summary>
public sealed class AuditAdministrationService(
    AuditDbContext db,
    IAuditArchivalService archival,
    IOptions<SecurityOptions> security,
    IAuditAdministrationAuthority? authority = null,
    ITamperDetectionService? tamperDetection = null) : IAuditAdministrationService
{
    private const int MaxEvents = 100_000;

    /// <inheritdoc />
    public Task<byte[]> ExportProofAsync(Guid eventId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new(AuditAdministrationOperation.ExportProof, EventId: eventId), async (scope, ct) =>
        {
            if (!await db.AuditEvents.AnyAsync(e => e.EventId == eventId && e.TenantId == scope.TenantId, ct))
                throw new UnauthorizedAccessException("Event is unavailable in the authorized tenant.");
            if (tamperDetection is null || !await db.AuditIntegrity.AnyAsync(e => e.EventId == eventId, ct))
                throw new AuditAdministrationConflictException("Integrity proof is unavailable for this event.");
            return await tamperDetection.ExportIntegrityProofAsync(eventId, ct);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AuditExportResult> ExportAsync(Guid userId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new(AuditAdministrationOperation.Export, SubjectUserId: userId), async (scope, ct) =>
        {
            // A metadata allow-list deliberately excludes free text, user names, natural entity keys,
            // event type/action strings supplied by arbitrary producers, and all JSON payloads.
            var rows = await SubjectEvents(scope.TenantId, userId).AsNoTracking()
                .OrderByDescending(e => e.InsertedDate).ThenBy(e => e.EventId).Take(MaxEvents + 1)
                .Select(e => new { e.EventId, e.InsertedDate, e.StartDate, e.EndDate, e.Duration })
                .ToListAsync(ct);
            if (rows.Count > MaxEvents)
                throw new AuditAdministrationConflictException("Export exceeds the event limit; narrow the export through the governed subject-request flow.");
            return new AuditExportResult
            {
                UserId = userId, Success = true, RecordCount = rows.Count,
                FileName = $"audit-export-{userId}.json",
                Data = JsonSerializer.SerializeToUtf8Bytes(new { Disclosure = "event-metadata-v1", Events = rows })
            };
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AnonymizationResult> AnonymizeAsync(Guid userId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(new(AuditAdministrationOperation.Anonymize, SubjectUserId: userId), async (scope, ct) =>
        {
            // Rewriting signed/hashed or envelope-owned content invalidates its proof/receipt. Pending
            // integrity work is protected too. This endpoint must never silently repair those proofs.
            if (security.Value.EnableTamperDetection)
                throw ProtectedEvidence();
            var events = await SubjectEvents(scope.TenantId, userId).AsTracking().Take(MaxEvents + 1).ToListAsync(ct);
            if (events.Count > MaxEvents)
                throw new AuditAdministrationConflictException("Anonymization exceeds the event limit.");
            var ids = events.Select(e => e.EventId).ToArray();
            if (await db.AuditIntegrity.AnyAsync(e => ids.Contains(e.EventId), ct)
                || await db.IntegrityWorkItems.AnyAsync(e => ids.Contains(e.EventId), ct)
                || await db.Set<AuditEnvelopeIdentity>().AnyAsync(e => e.Channel == "event" && ids.Contains(e.LogicalId), ct))
                throw ProtectedEvidence();
            foreach (var e in events)
            {
                e.User = "ANONYMIZED";
                e.EventType = "Audit.Anonymized";
                e.Action = null;
                e.EntityType = null;
                e.UserEnvName = null;
                e.MachineName = null;
                e.CallingMethodName = null;
                e.AssemblyName = null;
                e.Environment = null;
                e.RequestMethod = null;
                e.UserFullName = null;
                e.AspNetUserId = null;
                e.IpAddress = null;
                e.UserAgent = null;
                e.JsonData = null;
                e.AdditionalData = null;
                e.EntityId = null;
                e.RequestPath = null;
                e.CorrelationId = null;
            }
            // Persist the operation evidence in the same SaveChanges as the redaction below.
            return new AnonymizationResult { UserId = userId, Success = true, EventCount = events.Count,
                Message = "Anonymized unprotected event content; retained subject locators and event identity." };
        }, cancellationToken);

    /// <inheritdoc />
    public Task<AuditArchivalResult> ArchiveAsync(DateTimeOffset archiveBefore, CancellationToken cancellationToken = default)
    {
        if (archiveBefore == default || archiveBefore > DateTimeOffset.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(archiveBefore));
        return ExecuteAsync(new(AuditAdministrationOperation.Archive, ArchiveBefore: archiveBefore),
            (scope, ct) => archival.ArchiveTenantEventsAsync(scope.TenantId, scope.ActorId, archiveBefore, ct), cancellationToken);
    }

    /// <inheritdoc />
    public Task<AuditRestoreResult> RestoreAsync(string archiveId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(archiveId) || archiveId.Length > 100)
            throw new ArgumentException("Invalid archive identifier.", nameof(archiveId));
        return ExecuteAsync(new(AuditAdministrationOperation.Restore, ArchiveId: archiveId),
            (scope, ct) => archival.RestoreTenantEventsAsync(scope.TenantId, archiveId, ct), cancellationToken);
    }

    private IQueryable<AuditEventEntity> SubjectEvents(Guid tenantId, Guid userId) =>
        db.AuditEvents.Where(e => e.TenantId == tenantId && e.UserId == userId);

    private static AuditAdministrationConflictException ProtectedEvidence() => new(
        "Integrity-protected audit evidence cannot be rewritten. Use the governed erasure/retention flow.");

    private async Task<T> ExecuteAsync<T>(AuditAdministrationTarget target,
        Func<AuditAdministrationScope, CancellationToken, Task<T>> execute, CancellationToken ct)
    {
        if (target.SubjectUserId == Guid.Empty || target.EventId == Guid.Empty) throw new ArgumentException("Invalid subject identifier.");
        if (authority is null) throw new UnauthorizedAccessException("Audit administration authority is unavailable.");
        await using var permit = await authority.AcquireAsync(target, ct);
        var scope = permit.Scope;
        if (scope.TenantId == Guid.Empty || scope.ActorId == Guid.Empty || !Guid.TryParse(scope.CorrelationId, out _))
            throw new UnauthorizedAccessException("Audit administration scope is invalid.");
        await permit.CheckCurrentAsync(ct);
        // A durable attempt precedes operations that may commit in separate storage transactions.
        await EvidenceAsync(scope, target, "started", ct);
        // Serialize the protection checks and redaction with concurrent integrity/receipt writers.
        await using var transaction = target.Operation == AuditAdministrationOperation.Anonymize && db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct) : null;
        bool actionCompleted = false;
        try
        {
            var result = await execute(scope, ct);
            actionCompleted = true;
            var outcome = result switch
            {
                AuditArchivalResult a when !a.Success => "failed",
                AuditRestoreResult r when !r.Success => "failed",
                _ => "succeeded"
            };
            var archiveId = result is AuditArchivalResult archive ? archive.ArchiveId : target.ArchiveId;
            await EvidenceAsync(scope, target, outcome, ct, archiveId);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return result;
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                await transaction.DisposeAsync();
            }
            // Do not accidentally persist staged redactions while recording a failure.
            foreach (var entry in db.ChangeTracker.Entries<AuditEventEntity>().Where(e => e.State == EntityState.Modified).ToList())
                entry.State = EntityState.Detached;
            foreach (var entry in db.ChangeTracker.Entries<AuditSecurityEventEntity>()
                         .Where(e => e.State == EntityState.Added && e.Entity.CorrelationId == scope.CorrelationId).ToList())
                entry.State = EntityState.Detached;
            await EvidenceAsync(scope, target, actionCompleted ? "outcome_unknown" : "failed", CancellationToken.None);
            throw;
        }
    }

    private async Task EvidenceAsync(AuditAdministrationScope scope, AuditAdministrationTarget target,
        string outcome, CancellationToken ct, string? archiveId = null)
    {
        db.SecurityEvents.Add(new AuditSecurityEventEntity
        {
            EventType = SecurityEventType.AuditAdministration, Severity = SecurityEventSeverity.Low,
            TenantId = scope.TenantId, ActorUserId = scope.ActorId, SubjectUserId = target.SubjectUserId,
            CorrelationId = scope.CorrelationId, Operation = target.Operation.ToString(),
            DetectedAt = DateTimeOffset.UtcNow, DetectedBy = nameof(AuditAdministrationService),
            Message = $"Audit administration {outcome}",
            DetailsJson = JsonSerializer.Serialize(new { Outcome = outcome, ArchiveId = archiveId ?? target.ArchiveId, target.ArchiveBefore, target.EventId })
        });
        await db.SaveChangesAsync(ct);
    }
}
