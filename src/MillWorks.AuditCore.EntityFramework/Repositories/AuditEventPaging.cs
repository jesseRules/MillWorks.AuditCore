using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>Pages narrow event keys before loading large audit payloads.</summary>
public static class AuditEventPaging
{
    internal static IQueryable<Guid> SelectKeys(IOrderedQueryable<AuditEventEntity> ordered, int offset, int limit) =>
        ordered.Skip(offset).Take(limit).Select(static e => e.EventId);

    /// <summary>
    /// Keeps sort/offset work off the JSON payload. Hydration reapplies the original filters,
    /// and its database return order cannot change the accepted page's key order.
    /// </summary>
    public static async Task<List<AuditEventEntity>> ReadAsync(IQueryable<AuditEventEntity> filtered,
        IOrderedQueryable<AuditEventEntity> ordered, int offset, int limit, CancellationToken cancellationToken)
    {
        var ids = await SelectKeys(ordered, offset, limit).ToListAsync(cancellationToken);
        if (ids.Count == 0) return [];
        var rows = await filtered.Where(e => ids.Contains(e.EventId)).AsNoTracking()
            .ToDictionaryAsync(static e => e.EventId, cancellationToken);
        return ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
    }
}
