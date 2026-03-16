using System.Text.Json;
using MapsterMapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// security event service implementation
/// </summary>
/// <param name="securityEventRepository"></param>
/// <param name="auditContext"></param>
/// <param name="mapper"></param>
/// <param name="logger"></param>
/// <param name="configuration"></param>
public sealed class AuditSecurityEventService(
    ISecurityEventRepository securityEventRepository,
    IAuditContext auditContext,
    IMapper mapper,
    ILogger<AuditSecurityEventService> logger,
    IConfiguration configuration)
    : IAuditSecurityEventService
{
    /// <summary>
    /// Records a new security event.
    /// </summary>
    /// <param name="securityEvent"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SecurityEventDto> RecordEventAsync(
        SecurityEventDto securityEvent,
        CancellationToken cancellationToken = default)
    {
        var entity = mapper.Map<AuditSecurityEventEntity>(securityEvent);

        // Set metadata - handle cases where context isn't available
        entity.DetectedAt = DateTimeOffset.UtcNow;
        entity.DetectedBy = auditContext.UserEmail ?? "System";
        entity.IpAddress = auditContext.IpAddress;
        entity.Status = SecurityEventStatus.Open;

        // Serialize details
        if (securityEvent.Details.Any())
        {
            entity.DetailsJson = JsonSerializer.Serialize(securityEvent.Details);
        }

        // IMPORTANT: Save directly without triggering audit interceptor
        await securityEventRepository.AddAsync(entity, cancellationToken);
        await securityEventRepository.SaveChangesAsync(cancellationToken);

        logger.LogWarning(
            "Security Event Recorded: {EventType} - Severity: {Severity} - {Message}",
            entity.EventType, entity.Severity, entity.Message);

        // Send alert for critical events
        if (entity.Severity == SecurityEventSeverity.Critical)
        {
            await SendAlertAsync(mapper.Map<SecurityEventDto>(entity), cancellationToken);
        }

        return mapper.Map<SecurityEventDto>(entity);
    }

    /// <summary>
    /// Gets critical security events detected within the specified number of hours.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<SecurityEventDto>> GetCriticalEventsAsync(
        int hours = 24,
        CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var events = await securityEventRepository.GetByDateRangeAsync(
            since, DateTimeOffset.UtcNow, cancellationToken);

        return mapper.Map<IEnumerable<SecurityEventDto>>(
            events.Where(static e => e.Severity == SecurityEventSeverity.Critical));
    }

    /// <summary>
    /// Resolves a security event by updating its status and adding resolution details.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="resolution"></param>
    /// <param name="resolvedBy"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<SecurityEventDto?> ResolveEventAsync(
        Guid eventId,
        string resolution,
        string resolvedBy,
        CancellationToken cancellationToken = default)
    {
        var entity = await securityEventRepository.GetByIdAsync(eventId, cancellationToken);

        if (entity == null)
        {
            logger.LogWarning("Security event {EventId} not found", eventId);
            return null;
        }

        entity.Status = SecurityEventStatus.Resolved;
        entity.ResolvedAt = DateTimeOffset.UtcNow;
        entity.ResolvedBy = resolvedBy;
        entity.Resolution = resolution;

        await securityEventRepository.UpdateAsync(entity, cancellationToken);
        await securityEventRepository.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Security event {EventId} resolved by {ResolvedBy}",
            eventId, resolvedBy);

        return mapper.Map<SecurityEventDto>(entity);
    }

    /// <summary>
    /// Gets all open security events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<SecurityEventDto>> GetOpenEventsAsync(
        CancellationToken cancellationToken = default)
    {
        var events = await securityEventRepository.GetOpenEventsAsync(cancellationToken);
        return mapper.Map<IEnumerable<SecurityEventDto>>(events);
    }

    /// <summary>
    /// Sends an alert for a high-severity security event.
    /// </summary>
    /// <param name="securityEvent"></param>
    /// <param name="cancellationToken"></param>
    public Task SendAlertAsync(
        SecurityEventDto securityEvent,
        CancellationToken cancellationToken = default)
    {
        // Implement alert mechanism (email, SMS, Slack, etc.)
        var alertEnabled = configuration.GetValue<bool>("Security:AlertsEnabled", true);

        if (!alertEnabled) return Task.CompletedTask;

        logger.LogCritical(
            "SECURITY ALERT: {EventType} - {Message} - Event ID: {EventId}",
            securityEvent.EventType, securityEvent.Message, securityEvent.Id);

        // Alert delivery is intentionally limited to structured logging in v1.0.
        // Consumers should forward these log entries to their existing alerting
        // infrastructure (SIEM, PagerDuty, Slack, etc.) via a log sink.
        return Task.CompletedTask;
    }
}