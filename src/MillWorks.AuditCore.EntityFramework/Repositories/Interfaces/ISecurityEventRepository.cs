using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

/// <summary>
/// Interface for security event repository
/// </summary>
public interface ISecurityEventRepository : IRepository<AuditSecurityEventEntity>
{
    /// <summary>
    /// Gets security events by event type.
    /// </summary>
    /// <param name="eventType"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditSecurityEventEntity>> GetByEventTypeAsync(
        SecurityEventType eventType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets security events by severity.
    /// </summary>
    /// <param name="severity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditSecurityEventEntity>> GetBySeverityAsync(
        SecurityEventSeverity severity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all open security events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditSecurityEventEntity>> GetOpenEventsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets security events within a specified date range.
    /// </summary>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<AuditSecurityEventEntity>> GetByDateRangeAsync(
        DateTimeOffset startDate,
        DateTimeOffset endDate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a security event related to a specific audit event.
    /// </summary>
    /// <param name="auditEventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<AuditSecurityEventEntity?> GetByRelatedAuditEventAsync(
        Guid auditEventId,
        CancellationToken cancellationToken = default);
}