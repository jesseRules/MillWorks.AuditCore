using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.Mapping;

namespace MillWorks.AuditCore.Services.Query;

/// <summary>
/// Audit search service for querying and retrieving audit events based on various criteria.
/// </summary>
/// <param name="context"></param>
/// <param name="logger"></param>
public sealed class AuditSearchService(
    AuditDbContext context,
    ILogger<AuditSearchService> logger)
    : IAuditSearchService
{
    /// <summary>
    /// Searches audit events based on the provided criteria in the request.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchAuditEventsAsync(
        AuditSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Limit = QueryLimits.Clamp(request.Limit);
        if (request.Offset < 0) request.Offset = 0;

        logger.LogDebug(
            "Searching audit events with criteria: User={User}, EventType={EventType}, EntityType={EntityType}, StartDate={StartDate}, EndDate={EndDate}",
            request.User, request.EventType, request.EntityType, request.StartDate, request.EndDate);

        IQueryable<AuditEventEntity> query = BuildSearchQuery(request);

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Skip(request.Offset)
            .Take(request.Limit)
            .ToListAsync(cancellationToken);

        List<AuditEventDto> items = MapAndEnrichAuditEvents(events);

        return new AuditEventsResponse
        {
            TotalItems = totalItems,
            Items = items,
            Offset = request.Offset,
            Limit = request.Limit,
            TotalPages = (int)Math.Ceiling((double)totalItems / request.Limit),
            CurrentPage = (request.Offset / request.Limit) + 1
        };
    }

    /// <summary>
    /// Gets a list of distinct users who have performed actions in the audit log.
    /// Results are limited to <see cref="QueryLimits.MaxDistinctValues"/> to prevent unbounded scans.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to 500 distinct usernames, ordered alphabetically.</returns>
    public async Task<List<string>> GetDistinctUsersAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.User != null && e.User != "")
            .Select(static e => e.User!)
            .Distinct()
            .OrderBy(static u => u)
            .Take(QueryLimits.MaxDistinctValues)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a list of distinct event types from the audit log.
    /// Results are limited to <see cref="QueryLimits.MaxDistinctValues"/> to prevent unbounded scans.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to 500 distinct event types, ordered alphabetically.</returns>
    public async Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.EventType != null && e.EventType != "")
            .Select(static e => e.EventType!)
            .Distinct()
            .OrderBy(static t => t)
            .Take(QueryLimits.MaxDistinctValues)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a list of distinct entity types that have been audited.
    /// Results are limited to <see cref="QueryLimits.MaxDistinctValues"/> to prevent unbounded scans.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to 500 distinct entity types, ordered alphabetically.</returns>
    public async Task<List<string>> GetDistinctEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.EntityType != null && e.EntityType != "")
            .Select(static e => e.EntityType!)
            .Distinct()
            .OrderBy(static t => t)
            .Take(QueryLimits.MaxDistinctValues)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Searches audit events by entity type and optional entity ID.
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="entityId"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchByEntityAsync(
        string entityType,
        string? entityId = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        limit = QueryLimits.Clamp(limit);
        if (offset < 0) offset = 0;

        // Don't use SearchAuditEventsAsync - query by EntityType directly
        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == entityType);

        if (!string.IsNullOrEmpty(entityId))
        {
            query = query.Where(e => e.EntityId == entityId);
        }

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        List<AuditEventDto> items = MapAndEnrichAuditEvents(events);

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
    /// Searches audit events by user and optional date range.
    /// </summary>
    /// <param name="username"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchByUserAsync(
        string username,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        AuditSearchRequest request = new AuditSearchRequest
        {
            User = username,
            StartDate = startDate,
            EndDate = endDate,
            Offset = offset,
            Limit = limit
        };

        return await SearchAuditEventsAsync(request, cancellationToken);
    }

    /// <summary>
    /// Builds the search query based on the provided request criteria.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    private IQueryable<AuditEventEntity> BuildSearchQuery(AuditSearchRequest request)
    {
        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking();

        // Date range filters
        if (request.StartDate.HasValue)
        {
            query = query.Where(e => e.InsertedDate >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(e => e.InsertedDate <= request.EndDate.Value);
        }

        // User filter
        if (!string.IsNullOrWhiteSpace(request.User))
        {
            query = query.Where(e => e.User == request.User || e.UserFullName == request.User);
        }

        // Event type filter
        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            query = query.Where(e => e.EventType == request.EventType);
        }

        // Entity type filter
        if (!string.IsNullOrWhiteSpace(request.EntityType))
        {
            query = query.Where(e => e.EntityType == request.EntityType);
        }

        // General search term — leading-wildcard LIKE cannot use indexes.
        // Require minimum length to avoid expensive full-table scans on short terms.
        if (string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            return query;
        }

        // If a search term was provided but is too short, return zero results
        // rather than silently dropping the filter and widening the query.
        if (request.SearchTerm.Length < QueryLimits.MinSearchTermLength)
        {
            return query.Where(static _ => false);
        }

        var pattern = $"%{request.SearchTerm}%";
        query = query.Where(e =>
            (e.JsonData != null && EF.Functions.Like(e.JsonData, pattern)) ||
            (e.User != null && EF.Functions.Like(e.User, pattern)) ||
            (e.UserFullName != null && EF.Functions.Like(e.UserFullName, pattern)) ||
            (e.EventType != null && EF.Functions.Like(e.EventType, pattern)) ||
            (e.EntityType != null && EF.Functions.Like(e.EntityType, pattern)) ||
            (e.EntityId != null && EF.Functions.Like(e.EntityId, pattern)) ||
            (e.AdditionalData != null && EF.Functions.Like(e.AdditionalData, pattern))
        );

        return query;
    }

    /// <summary>Maps and enriches events through the same reader used by detail endpoints.</summary>
    private List<AuditEventDto> MapAndEnrichAuditEvents(List<AuditEventEntity> events)
    {
        var reader = new AuditEventJsonReader(logger);
        return events.Select(entity =>
        {
            var dto = entity.ToDto();
            dto.Data = reader.Read(dto);
            return dto;
        }).ToList();
    }
}
