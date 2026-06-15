using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// AuditEventRepository provides methods to interact with audit events in the system, enabling comprehensive audit trail management and analysis.
/// </summary>
/// <param name="context"></param>
public sealed class AuditEventRepository(AuditDbContext context)
    : Repository<AuditEventEntity>(context), IAuditEventRepository
{
    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByEventTypeAsync(string eventType,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.EventType == eventType)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByUserIdAsync(Guid userId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.UserId == userId)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByAspNetUserIdAsync(string aspNetUserId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.AspNetUserId == aspNetUserId)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByEntityAsync(string entityType, string entityId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.EntityType == entityType && ae.EntityId == entityId)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByDateRangeAsync(DateTimeOffset startDate,
        DateTimeOffset endDate,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByCorrelationIdAsync(string correlationId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.CorrelationId == correlationId)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByTenantIdAsync(Guid tenantId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.TenantId == tenantId)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByActionAsync(string action,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.Action == action)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByMinimumDurationAsync(int minimumDuration,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.Duration.HasValue && ae.Duration.Value >= minimumDuration)
            .OrderByDescending(static ae => ae.Duration)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetByEnvironmentAsync(string environment,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.Environment == environment)
            .OrderByDescending(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetWithIntegrityAsync(IEnumerable<Guid> eventIds,
        CancellationToken cancellationToken = default)
    {
        var eventIdList = eventIds as IList<Guid> ?? eventIds.ToList();
        return await DbSet.AsNoTracking()
            .Include(static ae => ae.AuditIntegrity)
            .Where(ae => eventIdList.Contains(ae.EventId))
            .OrderByDescending(static ae => ae.InsertedDate)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditEventEntity>> GetForTamperDetectionAsync(DateTimeOffset fromDate,
        int maxResults = 10000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= fromDate)
            .OrderBy(static ae => ae.InsertedDate)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> EventExistsAsync(Guid auditEventEventId, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .AnyAsync(ae => ae.EventId == auditEventEventId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HashSet<Guid>> GetExistingEventIdsAsync(IEnumerable<Guid> eventIds, CancellationToken cancellationToken = default)
    {
        var idList = eventIds.ToList();
        if (idList.Count == 0)
            return [];

        var existing = await DbSet.AsNoTracking()
            .Where(ae => idList.Contains(ae.EventId))
            .Select(ae => ae.EventId)
            .ToListAsync(cancellationToken);

        return [..existing];
    }

    /// <summary>
    /// Gets distinct event types from audit events.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(static e => e.EventType != null)
            .Select(static e => e.EventType!)
            .Distinct()
            .OrderBy(static et => et)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<string>> GetDistinctUsersAsync(CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(static e => e.User != null && e.User != "")
            .Select(static e => e.User!)
            .Distinct()
            .OrderBy(static u => u)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> GetUniqueUserCountAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking()
            .Where(static e => e.User != null && e.User != "");

        if (predicate is not null)
            query = query.Where(predicate);

        return await query.Select(static e => e.User).Distinct().CountAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<KeyValuePair<string, int>>> GetEventTypeCountsAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        IQueryable<AuditEventEntity> query = DbSet.AsNoTracking();

        if (predicate is not null)
            query = query.Where(predicate);

        var grouped = await query
            .GroupBy(static e => e.EventType ?? "Unknown")
            .Select(static g => new { Type = g.Key, Count = g.Count() })
            .OrderByDescending(static x => x.Count)
            .ToListAsync(cancellationToken);

        return grouped
            .Select(static x => new KeyValuePair<string, int>(x.Type, x.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<KeyValuePair<string, int>>> GetTopUsersByActivityAsync(
        int take = 10,
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        var query = DbSet.AsNoTracking()
            .Where(static e => e.User != null && e.User != "");

        if (predicate is not null)
            query = query.Where(predicate);

        var grouped = await query
            .GroupBy(static e => e.User!)
            .Select(static g => new { User = g.Key, Count = g.Count() })
            .OrderByDescending(static x => x.Count)
            .Take(take)
            .ToListAsync(cancellationToken);

        return grouped
            .Select(static x => new KeyValuePair<string, int>(x.User, x.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<(DateTimeOffset Date, string EventType, int Count)>> GetDailyEventCountsAsync(
        DateTimeOffset startDate, DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var results = await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate && ae.InsertedDate.HasValue)
            .GroupBy(static ae => new { ae.InsertedDate!.Value.Date, EventType = ae.EventType ?? "Unknown" })
            .Select(static g => new { g.Key.Date, g.Key.EventType, Count = g.Count() })
            .OrderBy(static x => x.Date)
            .ToListAsync(cancellationToken);

        return results
            .Select(static r => (Date: (DateTimeOffset)r.Date, r.EventType, r.Count))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<List<(string User, string EventType, int Count)>> GetUserEventCountsAsync(
        DateTimeOffset startDate, DateTimeOffset endDate, int take = 20,
        CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate)
            .GroupBy(static ae => new { User = ae.User ?? "Unknown", EventType = ae.EventType ?? "Unknown" })
            .Select(static g => new ValueTuple<string, string, int>(g.Key.User, g.Key.EventType, g.Count()))
            .OrderByDescending(static x => x.Item3)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<AuditEventEntity> StreamByDateAsync(
        System.Linq.Expressions.Expression<Func<AuditEventEntity, bool>> predicate,
        CancellationToken cancellationToken = default)
    {
        return DbSet.AsNoTracking()
            .Where(predicate)
            .OrderBy(static ae => ae.InsertedDate)
            .AsAsyncEnumerable();
    }

    /// <inheritdoc />
    public async Task<(DateTimeOffset? OldestDate, DateTimeOffset? NewestDate)?> GetDateRangeBoundariesAsync(
        DateTimeOffset startDate, DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var result = await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate && ae.InsertedDate.HasValue)
            .GroupBy(static _ => 1)
            .Select(static g => new
            {
                Oldest = g.Min(static e => e.InsertedDate),
                Newest = g.Max(static e => e.InsertedDate)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result is null)
            return null;

        return (result.Oldest, result.Newest);
    }

    /// <inheritdoc />
    public async Task<(int TotalEvents, int UnprotectedEvents)> GetIntegrityStatusCountsAsync(
        DateTimeOffset startDate, DateTimeOffset endDate,
        CancellationToken cancellationToken = default)
    {
        var total = await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate)
            .CountAsync(cancellationToken);

        var unprotected = await DbSet.AsNoTracking()
            .Where(ae => ae.InsertedDate >= startDate && ae.InsertedDate <= endDate)
            .Where(ae => ae.AuditIntegrity == null)
            .CountAsync(cancellationToken);

        return (total, unprotected);
    }
}
