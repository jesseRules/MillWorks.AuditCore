using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Diagnostics;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Services.Query;

/// <summary>
/// Audit query service for retrieving and processing audit logs and events.
/// </summary>
/// <param name="context"></param>
/// <param name="logger"></param>
public sealed class AuditQueryService(
    AuditDbContext context,
    ILogger<AuditQueryService> logger)
    : IAuditQueryService
{
    /// <summary>
    /// Gets the audit trail for a specific entity type and ID.
    /// </summary>
    /// <param name="entityName">The entity type name.</param>
    /// <param name="entityId">The entity identifier.</param>
    /// <param name="maxResults">Maximum number of results to return. Clamped to QueryLimits.MaxPageSize.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The audit trail entries, ordered by most recent first.</returns>
    public async Task<IEnumerable<AuditLogDto>> GetEntityAuditTrailAsync(
        string entityName,
        Guid entityId,
        int maxResults = 1000,
        CancellationToken cancellationToken = default)
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditQuery,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.QueryType, "entity_trail");
        activity?.SetTag(AuditActivitySource.Tags.AuditEntityType, entityName);
        activity?.SetTag(AuditActivitySource.Tags.AuditEntityId, entityId.ToString());

        maxResults = QueryLimits.Clamp(maxResults);
        logger.LogDebug("Getting audit trail for {EntityName} with ID {EntityId}, max {MaxResults}",
            entityName, entityId, maxResults);

        List<AuditEventEntity> events = await context.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == entityName && e.EntityId == entityId.ToString())
            .OrderByDescending(static e => e.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);

        activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, events.Count);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

        return MapToAuditLogDtos(events);
    }

    /// <summary>
    /// Gets the activity log for a specific user, optionally filtered by date and limited to a specified number of records.
    /// </summary>
    /// <param name="userId"></param>
    /// <param name="fromDate"></param>
    /// <param name="take"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetUserActivityAsync(
        Guid userId,
        DateTimeOffset? fromDate = null,
        int take = 50,
        CancellationToken cancellationToken = default)
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditQuery,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.QueryType, "user_activity");
        activity?.SetTag(AuditActivitySource.Tags.AuditUserId, userId.ToString());

        take = QueryLimits.Clamp(take);
        logger.LogDebug("Getting user activity for user {UserId}", userId);

        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking()
            .Where(e => e.UserId == userId);

        if (fromDate.HasValue)
        {
            query = query.Where(e => e.InsertedDate >= fromDate.Value);
        }

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Take(take)
            .ToListAsync(cancellationToken);

        activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, events.Count);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

        return MapToAuditLogDtos(events);
    }

    /// <summary>
    /// Gets a paginated list of audit events.
    /// </summary>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> GetAuditEventsAsync(
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        using var activity = AuditActivitySource.Source.StartActivity(
            AuditActivitySource.Operations.AuditQuery,
            ActivityKind.Internal);

        activity?.SetTag(AuditActivitySource.Tags.QueryType, "paginated");

        limit = QueryLimits.Clamp(limit);
        if (offset < 0) offset = 0;

        logger.LogDebug("Getting audit events with offset {Offset} and limit {Limit}", offset, limit);

        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking();

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        List<AuditEventDto> items = events.Select(static x => x.ToDto()).ToList();

        // Parse JSON data for each event
        foreach (AuditEventDto item in items.Where(static item => !string.IsNullOrEmpty(item.JsonData)))
        {
            try
            {
                if (item.JsonData != null)
                    item.Data = JsonSerializer.Deserialize<AuditEventJsonDataParsedResponse>(item.JsonData);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse JSON data for event {EventId}", item.EventId);
            }
        }

        activity?.SetTag(AuditActivitySource.Tags.ProcessedCount, items.Count);
        activity?.SetTag(AuditActivitySource.Tags.Outcome, "success");

        return new AuditEventsResponse
        {
            TotalItems = totalItems,
            Items = items,
            Offset = offset,
            Limit = limit,
            TotalPages = (int)Math.Ceiling((double)totalItems / limit),
            CurrentPage = (offset / limit) + 1
        };
    }

    /// <summary>
    /// Gets a specific audit event by its ID.
    /// </summary>
    /// <param name="eventId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventDto?> GetAuditEventByIdAsync(
        Guid eventId,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Getting audit event by ID {EventId}", eventId);

        AuditEventEntity? auditEvent = await context.AuditEvents.AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);

        if (auditEvent == null)
        {
            logger.LogWarning("Audit event {EventId} not found", eventId);
            return null;
        }

        AuditEventDto dto = auditEvent.ToDto();

        if (string.IsNullOrEmpty(dto.JsonData)) return dto;
        try
        {
            dto.Data = JsonSerializer.Deserialize<AuditEventJsonDataParsedResponse>(dto.JsonData);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse JSON data for event {EventId}", eventId);
        }

        return dto;
    }

    /// <summary>
    /// Gets recent activity from the audit log.
    /// </summary>
    /// <param name="hours"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<IEnumerable<AuditLogDto>> GetRecentActivityAsync(
        int hours = 24,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset since = DateTimeOffset.UtcNow.AddHours(-hours);

        List<AuditEventEntity> events = await context.AuditEvents.AsNoTracking()
            .Where(e => e.InsertedDate >= since)
            .OrderByDescending(static e => e.InsertedDate)
            .Take(QueryLimits.MaxPageSize)
            .ToListAsync(cancellationToken);

        return MapToAuditLogDtos(events);
    }

    /// <summary>
    /// Gets a grouped audit trail for a specific entity.
    /// </summary>
    /// <param name="entityName"></param>
    /// <param name="entityId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<Dictionary<string, List<AuditLogDto>>> GetGroupedAuditTrailAsync(
        string entityName,
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        IEnumerable<AuditLogDto> events = await GetEntityAuditTrailAsync(
            entityName, entityId, QueryLimits.MaxPageSize, cancellationToken);

        return events
            .GroupBy(static e => e.Action.ToString())
            .ToDictionary(static g => g.Key, static g => g.ToList());
    }

    /// <summary>
    /// Maps AuditEventEntity objects to AuditLogDto objects.
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    private IEnumerable<AuditLogDto> MapToAuditLogDtos(IEnumerable<AuditEventEntity> events)
    {
        List<AuditLogDto> dtos = new List<AuditLogDto>();

        foreach (AuditEventEntity evt in events)
        {
            AuditLogDto dto = new AuditLogDto
            {
                Id = evt.EventId,
                EntityName = evt.EntityType ?? "Unknown",
                EntityId = Guid.TryParse(evt.EntityId, out Guid entityId) ? entityId : Guid.Empty,
                Action = MapEventTypeToAction(evt.EventType),
                Description = evt.EventType,
                UserAgent = evt.UserAgent,
                IpAddress = evt.IpAddress,
                CreatedAt = evt.InsertedDate ?? DateTimeOffset.UtcNow,
                CreatedById = evt.UserId ?? Guid.Empty
            };

            // Attach raw JSON data
            if (!string.IsNullOrEmpty(evt.JsonData))
            {
                dto.AdditionalData = evt.JsonData;
            }

            dtos.Add(dto);
        }

        return dtos;
    }

    /// <summary>
    /// Maps event type strings to AuditAction enums.
    /// </summary>
    /// <param name="eventType"></param>
    /// <returns></returns>
    private AuditAction MapEventTypeToAction(string? eventType)
    {
        if (string.IsNullOrEmpty(eventType))
            return AuditAction.Unknown;

        string[] parts = eventType.Split('.');
        string? action = parts.LastOrDefault()?.ToLowerInvariant();

        return action switch
        {
            "created" or "insert" => AuditAction.Created,
            "updated" or "update" or "modified" => AuditAction.Updated,
            "deleted" or "delete" or "removed" => AuditAction.Deleted,
            "viewed" or "read" or "accessed" => AuditAction.Viewed,
            "exported" => AuditAction.Exported,
            "imported" => AuditAction.Imported,
            _ => AuditAction.Unknown
        };
    }
}