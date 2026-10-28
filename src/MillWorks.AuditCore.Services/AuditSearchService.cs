using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories;
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
        if (request.EntityId?.Length > 256) throw new ArgumentException("Entity ID must be at most 256 characters.");
        request.Limit = QueryLimits.Clamp(request.Limit);
        if (request.Offset < 0) request.Offset = 0;

        logger.LogDebug(
            "Searching audit events with criteria: User={User}, EventType={EventType}, EntityType={EntityType}, StartDate={StartDate}, EndDate={EndDate}",
            request.User, request.EventType, request.EntityType, request.StartDate, request.EndDate);

        var condition = AuditWordSearch.BuildCondition(request.SearchTerm);
        if (condition is not null) await EnsureTextSearchReadyAsync(cancellationToken);
        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking()
            .Where(AuditEventSearchFilter.Build(request));

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await AuditEventPaging.ReadAsync(query,
            query.OrderByDescending(static e => e.InsertedDate).ThenByDescending(static e => e.EventId),
            request.Offset, request.Limit, cancellationToken);

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
        return await AuditEventDistinctValues.UsersAsync(context, QueryLimits.MaxDistinctValues, cancellationToken);
    }

    /// <summary>
    /// Gets a list of distinct event types from the audit log.
    /// Results are limited to <see cref="QueryLimits.MaxDistinctValues"/> to prevent unbounded scans.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Up to 500 distinct event types, ordered alphabetically.</returns>
    public async Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default)
    {
        return await AuditEventDistinctValues.EventTypesAsync(context, true, QueryLimits.MaxDistinctValues, cancellationToken);
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

        List<AuditEventEntity> events = await AuditEventPaging.ReadAsync(query,
            query.OrderByDescending(static e => e.InsertedDate).ThenByDescending(static e => e.EventId),
            offset, limit, cancellationToken);

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

    private async Task EnsureTextSearchReadyAsync(CancellationToken cancellationToken)
    {
        if (!context.Database.IsSqlServer()) throw new AuditSearchUnavailableException();
        var entity = context.Model.FindEntityType(typeof(AuditEventEntity))!;
        var table = $"[{entity.GetSchema()!.Replace("]", "]]")}].[{entity.GetTableName()!.Replace("]", "]]")}]";
        var ready = await context.Database.SqlQuery<int>($"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM sys.fulltext_indexes
                WHERE object_id = OBJECT_ID({table}) AND is_enabled = 1 AND has_crawl_completed = 1
                    AND 7 = (SELECT COUNT(*) FROM sys.fulltext_index_columns f
                        JOIN sys.columns c ON c.object_id = f.object_id AND c.column_id = f.column_id
                        WHERE f.object_id = OBJECT_ID({table}) AND f.language_id = 0
                            AND c.name IN ('JsonData', 'User', 'UserFullName', 'EventType', 'EntityType', 'EntityId', 'AdditionalData'))
            ) THEN 1 ELSE 0 END AS int) AS [Value]
            """).SingleAsync(cancellationToken);
        if (ready != 1) throw new AuditSearchUnavailableException();
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
