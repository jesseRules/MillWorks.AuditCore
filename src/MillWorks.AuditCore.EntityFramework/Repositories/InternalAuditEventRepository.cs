using Microsoft.EntityFrameworkCore;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// Internal repository for saving audit events without triggering the audit interceptor.
/// This prevents circular dependencies when the audit system logs its own operations.
/// </summary>
public sealed class InternalAuditEventRepository
{
    /// <summary>
    /// Context for database operations
    /// </summary>
    private readonly AuditDbContext _context;

    /// <summary>
    /// Creates a new internal audit event repository
    /// </summary>
    /// <param name="context">Database context</param>
    public InternalAuditEventRepository(AuditDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    /// <summary>
    /// Saves changes to the database while bypassing the audit interceptor.
    /// This prevents infinite recursion when saving audit events.
    /// </summary>
    /// <remarks>
    /// <see cref="AuditDbContext.SaveChangesAsync(CancellationToken)"/> already detects
    /// audit entities and sets <c>BypassAuditInterceptor = true</c> automatically,
    /// so no manual flag toggling is needed here.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of affected rows</returns>
    public async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Gets an audit event by ID without tracking
    /// </summary>
    /// <param name="eventId">Event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Audit event or null</returns>
    public async Task<AuditEventEntity?> GetByIdAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return await _context.AuditEvents
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.EventId == eventId, cancellationToken);
    }

    /// <summary>
    /// Checks if an audit event exists by ID
    /// </summary>
    /// <param name="eventId">Event ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>True if exists</returns>
    public async Task<bool> ExistsAsync(Guid eventId, CancellationToken cancellationToken = default)
    {
        return await _context.AuditEvents
            .AsNoTracking()
            .AnyAsync(e => e.EventId == eventId, cancellationToken);
    }

    /// <summary>
    /// Adds an audit event directly without triggering interceptors.
    /// Uses a bypass mechanism to prevent circular dependency.
    /// </summary>
    /// <param name="entity">Audit event to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The added entity</returns>
    public async Task<AuditEventEntity> AddAsync(AuditEventEntity entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);

        await _context.AuditEvents.AddAsync(entity, cancellationToken);
        return entity;
    }

    /// <summary>
    /// Adds multiple audit events directly without triggering interceptors.
    /// </summary>
    /// <param name="entities">Audit events to add</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task AddRangeAsync(IEnumerable<AuditEventEntity> entities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entities);

        await _context.AuditEvents.AddRangeAsync(entities, cancellationToken);
    }
}
