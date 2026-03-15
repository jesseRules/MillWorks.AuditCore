using MillWorks.AuditCore.Abstractions.Dto;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Interface for security event service
/// </summary>
public interface IAuditSecurityEventService
{
    /// <summary>
    /// Records a new security event.
    /// </summary>
    /// <param name="securityEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<SecurityEventDto> RecordEventAsync(
        SecurityEventDto securityEvent,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets critical security events detected within the specified number of hours.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<SecurityEventDto>> GetCriticalEventsAsync(
        int hours = 24,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a security event by updating its status and adding resolution details.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="resolution"></param>
    /// <param name="resolvedBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<SecurityEventDto?> ResolveEventAsync(
        Guid eventId,
        string resolution,
        string resolvedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all open security events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<SecurityEventDto>> GetOpenEventsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends an alert for a high-severity security event.
    /// </summary>
    /// <param name="securityEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task SendAlertAsync(
        SecurityEventDto securityEvent,
        CancellationToken cancellationToken = default);
}