using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// Security event repository implementation
/// </summary>
/// <param name="context"></param>
public sealed class SecurityEventRepository(AuditDbContext context)
    : Repository<AuditSecurityEventEntity>(context), ISecurityEventRepository
{
    /// <summary>
    /// Gets security events by event type.
    /// </summary>
    /// <remarks>Returns all matching rows with no limit. For large datasets, prefer <see cref="Repository{T}.GetPagedAsync"/>.</remarks>
    /// <param name="eventType"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditSecurityEventEntity>> GetByEventTypeAsync(
        SecurityEventType eventType,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(e => e.EventType == eventType)
            .OrderByDescending(static e => e.DetectedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets security events by severity.
    /// </summary>
    /// <remarks>Returns all matching rows with no limit. For large datasets, prefer <see cref="Repository{T}.GetPagedAsync"/>.</remarks>
    /// <param name="severity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditSecurityEventEntity>> GetBySeverityAsync(
        SecurityEventSeverity severity,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(e => e.Severity == severity)
            .OrderByDescending(static e => e.DetectedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets security events within a specified date range.
    /// </summary>
    /// <remarks>Returns all matching rows with no limit. For large datasets, prefer <see cref="Repository{T}.GetPagedAsync"/>.</remarks>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditSecurityEventEntity>> GetByDateRangeAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(e => e.DetectedAt >= startDate && e.DetectedAt <= endDate)
            .OrderByDescending(static e => e.DetectedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a security event related to a specific audit event.
    /// </summary>
    /// <param name="auditEventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditSecurityEventEntity?> GetByRelatedAuditEventAsync(
        Guid auditEventId,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Include(static e => e.RelatedAuditEvent)
            .FirstOrDefaultAsync(e => e.RelatedAuditEventId == auditEventId, cancellationToken);
    }

    /// <summary>
    /// Gets security events by severity within a date range (server-side filtering).
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditSecurityEventEntity>> GetBySeverityAndDateRangeAsync(
        SecurityEventSeverity severity,
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(e => e.Severity == severity &&
                        e.DetectedAt >= startDate &&
                        e.DetectedAt <= endDate)
            .OrderByDescending(static e => e.DetectedAt)
            .ToListAsync(cancellationToken);
    }
}