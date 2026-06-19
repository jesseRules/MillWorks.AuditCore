using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Primitives;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;

namespace MillWorks.AuditCore.EntityFramework.Repositories;

/// <summary>
/// Repository class implementing basic CRUD operations for entities of type T.
/// Enhanced with concurrency handling support while maintaining backward compatibility.
/// </summary>
/// <param name="context"></param>
/// <typeparam name="T"></typeparam>
public class Repository<T>(AuditDbContext context) : IRepository<T>
    where T : class
{
    /// <summary>
    /// Context for database operations.
    /// </summary>
    private readonly AuditDbContext _context = context;

    /// <summary>
    /// Database context accessible to derived repositories.
    /// </summary>
    protected AuditDbContext Context => _context;

    /// <summary>
    /// DbSet for the entity type T, allowing access to the database table for T.
    /// </summary>
    protected readonly DbSet<T> DbSet = context.Set<T>();

    /// <summary>
    /// Current database transaction, if any.
    /// </summary>
    private IDbContextTransaction? _currentTransaction;

    /// <summary>
    /// Logger for repository operations (optional)
    /// </summary>
    protected ILogger<Repository<T>>? Logger { get; set; }

    /// <summary>
    /// Transaction currently active on this repository's <see cref="AuditDbContext"/>.
    /// Falls back from the transaction this instance opened to whatever <c>Database.CurrentTransaction</c>
    /// reports on the shared context, so a second repository on the same context observes the
    /// same transaction without having to open it itself. This is authoritative for the one-context-per-
    /// connection model AuditCore uses; if a caller enrolls two <see cref="AuditDbContext"/>
    /// instances on the same underlying connection, they must coordinate via <c>UseTransaction</c>
    /// so each context's own <c>Database.CurrentTransaction</c> stays accurate.
    /// </summary>
    public IDbContextTransaction? CurrentTransaction => _currentTransaction ?? _context.Database.CurrentTransaction;

    #region Core Methods

    /// <summary>
    /// Gets an entity by its unique identifier.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        await DbSet.FindAsync([id], cancellationToken);

    /// <summary>
    /// Gets all entities of type T with optional no-tracking for read-only scenarios.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default) =>
        await DbSet.AsNoTracking().ToListAsync(cancellationToken);

    /// <summary>
    /// Finds entities based on a predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        await DbSet.AsNoTracking().Where(predicate).ToListAsync(cancellationToken);

    /// <summary>
    /// Finds the first entity that matches the predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        await DbSet.AsNoTracking().FirstOrDefaultAsync(predicate, cancellationToken);

    /// <summary>
    /// Checks if any entity matches the given predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        await DbSet.AnyAsync(predicate, cancellationToken);

    /// <summary>
    /// Counts the number of entities that match the given predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default)
    {
        return predicate == null
            ? await DbSet.CountAsync(cancellationToken)
            : await DbSet.CountAsync(predicate, cancellationToken);
    }

    /// <summary>
    /// Adds a new entity. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<T> AddAsync(T entity, CancellationToken cancellationToken = default)
    {
        await DbSet.AddAsync(entity, cancellationToken);
        return entity;
    }

    /// <summary>
    /// Adds a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<IEnumerable<T>> AddRangeAsync(IEnumerable<T> entities,
        CancellationToken cancellationToken = default)
    {
        T[] entitiesArray = entities as T[] ?? entities.ToArray();
        await DbSet.AddRangeAsync(entitiesArray, cancellationToken);
        return entitiesArray;
    }

    /// <summary>
    /// Updates an existing entity. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual Task<T> UpdateAsync(T entity, CancellationToken cancellationToken = default)
    {
        DbSet.Update(entity);
        return Task.FromResult(entity);
    }

    /// <summary>
    /// Updates a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual Task<IEnumerable<T>> UpdateRangeAsync(IEnumerable<T> entities,
        CancellationToken cancellationToken = default)
    {
        T[] entitiesArray = entities as T[] ?? entities.ToArray();
        DbSet.UpdateRange(entitiesArray);
        return Task.FromResult<IEnumerable<T>>(entitiesArray);
    }

    /// <summary>
    /// Deletes an entity by its unique identifier. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    public virtual async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        T? entity = await GetByIdAsync(id, cancellationToken);
        if (entity != null)
        {
            await DeleteAsync(entity, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes an entity by its unique identifier, recording who performed the deletion.
    /// For entities inheriting from <see cref="AuditAggregateRoot"/>, this sets <c>DeletedById</c>
    /// in addition to <c>IsDeleted</c> and <c>DeletedAt</c>.
    /// Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="id">Entity ID</param>
    /// <param name="deletedBy">ID of the user performing the deletion</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public virtual async Task DeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default)
    {
        T? entity = await GetByIdAsync(id, cancellationToken);
        if (entity != null)
        {
            await DeleteAsync(entity, deletedBy, cancellationToken);
        }
    }

    /// <summary>
    /// Deletes an entity from the database. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    public virtual Task DeleteAsync(T entity, CancellationToken cancellationToken = default)
    {
        // Check if entity supports soft delete
        if (entity is AuditAggregateRoot baseEntity)
        {
            baseEntity.IsDeleted = true;
            baseEntity.DeletedAt = DateTimeOffset.UtcNow;
            DbSet.Update(entity);
        }
        else
        {
            DbSet.Remove(entity);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes an entity, recording who performed the deletion.
    /// For entities inheriting from <see cref="AuditAggregateRoot"/>, this calls
    /// <see cref="AuditAggregateRoot.Delete(Guid)"/> which sets <c>DeletedById</c>,
    /// <c>IsDeleted</c>, and <c>DeletedAt</c>, and raises a domain event.
    /// Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity">Entity to delete</param>
    /// <param name="deletedBy">ID of the user performing the deletion</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public virtual Task DeleteAsync(T entity, Guid deletedBy, CancellationToken cancellationToken = default)
    {
        if (entity is AuditAggregateRoot baseEntity)
        {
            baseEntity.Delete(deletedBy);
            DbSet.Update(entity);
        }
        else
        {
            DbSet.Remove(entity);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Refreshes the context by reloading all tracked entities.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public virtual async Task RefreshContextAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in _context.ChangeTracker.Entries())
        {
            await entry.ReloadAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Clears the change tracker, removing all tracked entities.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual Task ClearChangeTrackerAsync(CancellationToken cancellationToken = default)
    {
        _context.ChangeTracker.Clear();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task DetachAsync(T entity, CancellationToken cancellationToken = default)
    {
        _context.Entry(entity).State = EntityState.Detached;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual Task DetachRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        foreach (var entity in entities)
        {
            _context.Entry(entity).State = EntityState.Detached;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    public virtual Task DeleteRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default)
    {
        foreach (T entity in entities)
        {
            // DeleteAsync(entity) is synchronous (returns Task.CompletedTask), no need to await
            DeleteAsync(entity, cancellationToken);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public virtual async Task<int> ExecuteDeleteWhereAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default) =>
        await DbSet.Where(predicate).ExecuteDeleteAsync(cancellationToken);

    /// <summary>
    /// Saves all changes made in this context to the database asynchronously.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public virtual async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        await _context.SaveChangesAsync(cancellationToken);

    /// <summary>
    /// Gets a paged result of entities with optional filtering, ordering, and includes.
    /// </summary>
    /// <param name="pageNumber">The page number (1-based)</param>
    /// <param name="pageSize">The number of items per page</param>
    /// <param name="predicate">Optional predicate to filter entities</param>
    /// <param name="orderBy">Optional ordering function</param>
    /// <param name="includes">Optional navigation properties to include</param>
    /// <returns>A tuple containing the paged items and total count</returns>
    public virtual async Task<(IEnumerable<T> Items, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        Expression<Func<T, bool>>? predicate = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        params Expression<Func<T, object>>[] includes)
    {
        // Validate parameters
        if (pageNumber < 1) throw new ArgumentException("Page number must be greater than 0", nameof(pageNumber));
        if (pageSize < 1) throw new ArgumentException("Page size must be greater than 0", nameof(pageSize));

        // Start with DbSet and apply includes
        IQueryable<T> query = includes.Aggregate<Expression<Func<T, object>>?, IQueryable<T>>(
            DbSet.AsNoTracking(), static (current, include) => current.Include(navigationPropertyPath: include!));

        // Apply filtering
        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        // Get total count before applying paging
        int totalCount = await query.CountAsync();

        // Apply ordering — required for deterministic paging
        if (orderBy != null)
        {
            query = orderBy(query);
        }
        else
        {
            // Fall back to ordering by primary key for deterministic results
            var keyProperty = Context.Model.FindEntityType(typeof(T))?.FindPrimaryKey()?.Properties.FirstOrDefault();
            if (keyProperty != null)
            {
                var parameter = Expression.Parameter(typeof(T), "e");
                var property = Expression.Property(parameter, keyProperty.Name);
                var converted = Expression.Convert(property, typeof(object));
                var lambda = Expression.Lambda<Func<T, object>>(converted, parameter);
                query = query.OrderBy(lambda);
            }
        }

        // Apply paging
        List<T> items = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    /// <summary>
    /// Gets a paginated list of entities using offset-based pagination.
    /// Unlike <see cref="GetPagedAsync"/> which uses page numbers, this method
    /// correctly handles non-page-aligned offsets (e.g., offset=75, limit=50 returns rows 75-124).
    /// </summary>
    public virtual async Task<(IEnumerable<T> Items, int TotalCount)> GetByOffsetAsync(
        int offset,
        int limit,
        Expression<Func<T, bool>>? predicate = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0) throw new ArgumentException("Offset cannot be negative", nameof(offset));
        if (limit < 1) throw new ArgumentException("Limit must be greater than 0", nameof(limit));

        IQueryable<T> query = DbSet.AsNoTracking();

        if (predicate != null)
        {
            query = query.Where(predicate);
        }

        int totalCount = await query.CountAsync(cancellationToken);

        if (orderBy != null)
        {
            query = orderBy(query);
        }
        else
        {
            Logger?.LogDebug("GetByOffsetAsync called without orderBy; results may be non-deterministic");
        }

        List<T> items = await query
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    /// <summary>
    /// Gets a queryable for building complex queries
    /// </summary>
    /// <returns>IQueryable of T</returns>
    public virtual IQueryable<T> GetQueryable() => DbSet.AsQueryable();

    #endregion

    #region Enhanced Concurrency Handling Methods

    /// <summary>
    /// Saves changes with automatic retry on concurrency conflicts.
    /// </summary>
    /// <remarks>
    /// <b>Warning:</b> On concurrency conflict, this method reloads conflicting entities from the database,
    /// overwriting any in-memory changes with database values ("database-wins" semantics).
    /// User modifications made before the call will be silently discarded on retry.
    /// If you need "client-wins" or merge semantics, use <see cref="ExecuteOptimisticUpdateAsync(Guid, Action{T}, int, CancellationToken)"/> instead.
    /// </remarks>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of affected entities</returns>
    public virtual async Task<int> SaveChangesWithRetryAsync(
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        var retryCount = 0;
        var baseDelay = TimeSpan.FromMilliseconds(100);

        while (retryCount < maxRetries)
        {
            try
            {
                return await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    Logger?.LogError(ex,
                        "Failed to save changes after {MaxRetries} attempts due to concurrency conflicts",
                        maxRetries);
                    throw;
                }

                // Calculate exponential backoff
                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1));

                Logger?.LogWarning(
                    "Concurrency conflict on save. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    retryCount, maxRetries, delay.TotalMilliseconds);

                // Reload all modified entities
                foreach (var entry in ex.Entries)
                {
                    await entry.ReloadAsync(cancellationToken);
                }

                // Wait before retrying
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Failed to save changes after {maxRetries} retries");
    }

    /// <summary>
    /// Updates an entity with automatic retry on concurrency conflicts.
    /// </summary>
    /// <param name="entity">Entity to update</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entity</returns>
    public virtual async Task<T> UpdateWithRetryAsync(
        T entity,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        DbSet.Update(entity);
        await SaveChangesWithRetryAsync(maxRetries, cancellationToken);
        return entity;
    }

    /// <summary>
    /// Executes an update operation with optimistic concurrency control.
    /// Reloads the entity on each retry attempt.
    /// </summary>
    /// <param name="id">Entity ID</param>
    /// <param name="updateAction">Action to apply updates to the entity</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entity</returns>
    public virtual async Task<T?> ExecuteOptimisticUpdateAsync(
        Guid id,
        Action<T> updateAction,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        var retryCount = 0;
        var baseDelay = TimeSpan.FromMilliseconds(100);

        while (retryCount < maxRetries)
        {
            try
            {
                // Clear change tracker to ensure fresh load
                _context.ChangeTracker.Clear();

                // Load entity fresh from database
                var entity = await GetByIdAsync(id, cancellationToken);
                if (entity == null)
                {
                    return null;
                }

                // Apply updates
                updateAction(entity);

                // Mark as modified if not already tracked
                if (_context.Entry(entity).State == EntityState.Detached)
                {
                    DbSet.Update(entity);
                }

                // Save changes
                await SaveChangesAsync(cancellationToken);

                Logger?.LogDebug("Successfully updated entity {EntityId} after {RetryCount} attempts",
                    id, retryCount + 1);

                return entity;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    Logger?.LogError(ex,
                        "Failed to execute optimistic update for entity {EntityId} after {MaxRetries} attempts",
                        id, maxRetries);
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1));

                Logger?.LogWarning(
                    "Concurrency conflict for entity {EntityId}. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    id, retryCount, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Failed to update entity {id} after {maxRetries} retries");
    }

    /// <summary>
    /// Executes an update operation with predicate-based optimistic concurrency control.
    /// </summary>
    /// <param name="predicate">Predicate to find the entity</param>
    /// <param name="updateAction">Action to apply updates to the entity</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entity or null if not found</returns>
    public virtual async Task<T?> ExecuteOptimisticUpdateAsync(
        Expression<Func<T, bool>> predicate,
        Action<T> updateAction,
        int maxRetries = 3,
        CancellationToken cancellationToken = default)
    {
        var retryCount = 0;
        var baseDelay = TimeSpan.FromMilliseconds(100);

        while (retryCount < maxRetries)
        {
            try
            {
                // Clear change tracker to ensure fresh load
                _context.ChangeTracker.Clear();

                // Load entity fresh from database with tracking
                var entity = await DbSet.FirstOrDefaultAsync(predicate, cancellationToken);
                if (entity == null)
                {
                    return null;
                }

                // Apply updates
                updateAction(entity);

                // Save changes
                await SaveChangesAsync(cancellationToken);

                Logger?.LogDebug("Successfully updated entity after {RetryCount} attempts", retryCount + 1);

                return entity;
            }
            catch (DbUpdateConcurrencyException ex)
            {
                retryCount++;

                if (retryCount >= maxRetries)
                {
                    Logger?.LogError(ex,
                        "Failed to execute optimistic update after {MaxRetries} attempts",
                        maxRetries);
                    throw;
                }

                var delay = TimeSpan.FromMilliseconds(
                    baseDelay.TotalMilliseconds * Math.Pow(2, retryCount - 1));

                Logger?.LogWarning(
                    "Concurrency conflict. Retry {RetryCount}/{MaxRetries} after {DelayMs}ms",
                    retryCount, maxRetries, delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Failed to update entity after {maxRetries} retries");
    }

    /// <summary>
    /// Reloads an entity from the database, discarding any local changes.
    /// </summary>
    /// <param name="entity">Entity to reload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public virtual async Task ReloadEntityAsync(T entity, CancellationToken cancellationToken = default) =>
        await _context.Entry(entity).ReloadAsync(cancellationToken);

    #endregion

    #region Enhanced Transaction Support

    /// <summary>
    /// Begins a new database transaction
    /// </summary>
    /// <returns>The database transaction</returns>
    public virtual async Task<IDbContextTransaction> BeginTransactionAsync() =>
        await BeginTransactionAsync(CancellationToken.None);

    /// <summary>
    /// Begins a new database transaction with cancellation support
    /// </summary>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The database transaction</returns>
    public virtual async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        // Two guards, one message: reject if THIS repository already owns a transaction,
        // or if the shared DbContext already has one (opened by another repository on the
        // same scope). Without the second check, a sibling repo's active transaction would
        // silently co-exist with a new one begun here — producing a no-op nested begin on
        // some providers or an ambiguous "connection already in a transaction" deep inside
        // EF on others. Callers in that position should join via CurrentTransaction instead.
        if (_currentTransaction is not null || _context.Database.CurrentTransaction is not null)
        {
            throw new InvalidOperationException(
                "A transaction is already in progress on this DbContext. " +
                "Repositories sharing a DbContext must join the existing transaction " +
                "(via CurrentTransaction) rather than opening a nested one.");
        }

        _currentTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        return _currentTransaction;
    }

    /// <summary>
    /// Commits the current transaction
    /// </summary>
    public virtual async Task CommitTransactionAsync()
    {
        try
        {
            await SaveChangesAsync();

            if (_currentTransaction != null)
            {
                await _currentTransaction.CommitAsync();
            }
        }
        catch
        {
            await RollbackTransactionAsync();
            throw;
        }
        finally
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }
        }
    }

    /// <summary>
    /// Rolls back the current transaction
    /// </summary>
    public virtual async Task RollbackTransactionAsync()
    {
        try
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.RollbackAsync();
            }
        }
        finally
        {
            if (_currentTransaction != null)
            {
                await _currentTransaction.DisposeAsync();
                _currentTransaction = null;
            }
        }
    }

    /// <summary>
    /// Executes an action within a transaction with automatic rollback on failure
    /// </summary>
    /// <param name="action">The action to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public virtual async Task ExecuteInTransactionAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            try
            {
                await action();
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            finally
            {
                _currentTransaction = null;
            }
        });
    }

    /// <summary>
    /// Executes a function within a transaction with automatic rollback on failure
    /// </summary>
    /// <typeparam name="TResult">The return type</typeparam>
    /// <param name="func">The function to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the function</returns>
    public virtual async Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<Task<TResult>> func,
        CancellationToken cancellationToken = default)
    {
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await func();
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
            finally
            {
                _currentTransaction = null;
            }
        });
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposed flag
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Dispose
    /// </summary>
    /// <param name="disposing"></param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            _currentTransaction?.Dispose();
            // Do not dispose _context here - it is owned by the DI container
        }

        _disposed = true;
    }

    /// <summary>
    /// Dispose
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Dispose
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }

        // Do not dispose _context here - it is owned by the DI container

        Dispose(false);
        GC.SuppressFinalize(this);
    }

    #endregion
}
