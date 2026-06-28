using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// security event service implementation
/// </summary>
/// <param name="securityEventRepository"></param>
/// <param name="auditContext"></param>
/// <param name="logger"></param>
/// <param name="configuration"></param>
public sealed class AuditSecurityEventService(
    ISecurityEventRepository securityEventRepository,
    IAuditContext auditContext,
    ILogger<AuditSecurityEventService> logger,
    IConfiguration configuration)
    : IAuditSecurityEventService
{
    private const int MaxMessageLength = 500;
    private const int MaxDetailsJsonLength = 4000;

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
        var entity = securityEvent.ToEntity();

        // Set metadata - handle cases where context isn't available
        entity.DetectedAt = DateTimeOffset.UtcNow;
        entity.DetectedBy = auditContext.UserEmail ?? "System";
        entity.Status = SecurityEventStatus.Open;

        // Privacy-preserving source metadata: if SourceIpHash is populated and IpAddress
        // is null on the incoming DTO, do not stamp raw IpAddress from auditContext.
        // This allows break-glass callers to record hash-only metadata.
        if (string.IsNullOrEmpty(entity.SourceIpHash) || !string.IsNullOrEmpty(securityEvent.IpAddress))
        {
            entity.IpAddress = securityEvent.IpAddress ?? auditContext.IpAddress;
        }

        // Enforce entity size limits to prevent persistence failures
        // Use TruncateSafe to avoid splitting surrogate pairs (#10)
        if (entity.Message.Length > MaxMessageLength)
        {
            logger.LogWarning(
                "Security event message truncated from {Original} to {Max} chars for event type {EventType}",
                entity.Message.Length, MaxMessageLength, entity.EventType);
            entity.Message = SensitiveContentSanitizer.TruncateSafe(entity.Message, MaxMessageLength);
        }

        // Serialize details with size guard - must produce valid JSON
        if (securityEvent.Details.Any())
        {
            var serialized = JsonSerializer.Serialize(securityEvent.Details);
            if (serialized.Length > MaxDetailsJsonLength)
            {
                logger.LogWarning(
                    "Security event details exceeded {Max} chars ({Actual}), storing summary for event type {EventType}",
                    MaxDetailsJsonLength, serialized.Length, entity.EventType);

                // Store a valid JSON summary instead of truncated invalid JSON
                entity.DetailsJson = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["_truncated"] = true,
                    ["_originalLength"] = serialized.Length,
                    ["_keyCount"] = securityEvent.Details.Count,
                    ["_keys"] = securityEvent.Details.Keys.Take(20).ToList()
                });
            }
            else
            {
                entity.DetailsJson = serialized;
            }
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
            await SendAlertAsync(entity.ToDto(), cancellationToken);
        }

        return entity.ToDto();
    }

    /// <summary>
    /// Gets critical security events detected within the specified number of hours.
    /// Uses server-side filtering for efficiency on busy systems.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<SecurityEventDto>> GetCriticalEventsAsync(
        int hours = 24,
        CancellationToken cancellationToken = default)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-hours);
        var events = await securityEventRepository.GetBySeverityAndDateRangeAsync(
            SecurityEventSeverity.Critical, since, DateTimeOffset.UtcNow, cancellationToken);

        return events.Select(static x => x.ToDto()).ToList();
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
        return events.Select(static x => x.ToDto()).ToList();
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