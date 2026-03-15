using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// AuditLogRepository provides methods to interact with audit logs in the project management system, allowing retrieval of audit trails for entities and user activities.
/// </summary>
/// <param name="context"></param>
public sealed class AuditLogRepository(AuditApplicationDbContext context)
    : Repository<AuditLogEntity>(context), IAuditLogRepository
{
    /// <inheritdoc />
    public async Task<IEnumerable<AuditLogEntity>> GetEntityAuditTrailAsync(string entityName, Guid entityId,
        int maxResults = 1000, CancellationToken cancellationToken = default)
    {
        return await DbSet.AsNoTracking()
            .Where(a => a.EntityName == entityName && a.EntityId == entityId)
            .OrderByDescending(static a => a.CreatedAt)
            .Take(maxResults)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<AuditLogEntity>> GetUserActivityAsync(Guid userId, DateTimeOffset? fromDate = null,
        int take = 100, CancellationToken cancellationToken = default)
    {
        IQueryable<AuditLogEntity> query = DbSet.AsNoTracking().Where(a => a.CreatedById == userId);

        if (fromDate.HasValue)
        {
            query = query.Where(a => a.CreatedAt >= fromDate.Value);
        }

        return await query
            .OrderByDescending(static a => a.CreatedAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<List<KeyValuePair<string, int>>> GetActionCountsAsync(Guid? userId = null,
        DateTimeOffset? fromDate = null, CancellationToken cancellationToken = default)
    {
        IQueryable<AuditLogEntity> query = DbSet.AsNoTracking();

        if (userId.HasValue)
            query = query.Where(a => a.CreatedById == userId.Value);

        if (fromDate.HasValue)
            query = query.Where(a => a.CreatedAt >= fromDate.Value);

        var results = await query
            .GroupBy(static a => a.Action)
            .Select(static g => new { Action = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return results
            .Select(static r => new KeyValuePair<string, int>(r.Action.ToString(), r.Count))
            .ToList();
    }
}