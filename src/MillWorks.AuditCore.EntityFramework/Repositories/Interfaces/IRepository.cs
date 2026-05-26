using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Storage;

namespace MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;


/// <summary>
/// Interface for a generic repository pattern that provides basic CRUD operations for entities of type T.
/// Enhanced with concurrency handling support.
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IRepository<T> where T : class
{
    /// <summary>
    /// Gets an entity by its unique identifier.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all entities of type T with no-tracking for read-only scenarios.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<T>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds entities based on a predicate expression asynchronously with no-tracking.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<T>> FindAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds the first entity that matches the predicate expression asynchronously with no-tracking.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T?> FirstOrDefaultAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if any entity matches the given predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<bool> ExistsAsync(Expression<Func<T, bool>> predicate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the number of entities that match the given predicate expression asynchronously.
    /// </summary>
    /// <param name="predicate"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<int> CountAsync(Expression<Func<T, bool>>? predicate = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a new entity. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T> AddAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<T>> AddRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an existing entity. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<T> UpdateAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IEnumerable<T>> UpdateRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity by its unique identifier. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="id"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity by its unique identifier, recording who performed the deletion.
    /// For entities inheriting from <see cref="MillWorks.AuditCore.EntityFramework.Primitives.AuditAggregateRoot"/>,
    /// this sets <c>DeletedById</c> in addition to <c>IsDeleted</c> and <c>DeletedAt</c>.
    /// Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="id">Entity ID</param>
    /// <param name="deletedBy">ID of the user performing the deletion</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(Guid id, Guid deletedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity from the repository. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task DeleteAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an entity, recording who performed the deletion.
    /// For entities inheriting from <see cref="MillWorks.AuditCore.EntityFramework.Primitives.AuditAggregateRoot"/>,
    /// this calls <c>Delete(deletedBy)</c> which sets <c>DeletedById</c>, <c>IsDeleted</c>, and <c>DeletedAt</c>.
    /// Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entity">Entity to delete</param>
    /// <param name="deletedBy">ID of the user performing the deletion</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task DeleteAsync(T entity, Guid deletedBy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a range of entities. Call SaveChangesAsync() to persist to database.
    /// </summary>
    /// <param name="entities"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task DeleteRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bulk-deletes all entities matching the predicate using a single SQL DELETE statement.
    /// Does not load entities into memory. Does not require SaveChangesAsync().
    /// </summary>
    /// <param name="predicate">Filter for entities to delete</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of rows deleted</returns>
    Task<int> ExecuteDeleteWhereAsync(Expression<Func<T, bool>> predicate,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves all changes made in the context to the database asynchronously.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a paginated list of entities with no-tracking based on the specified parameters.
    /// </summary>
    /// <param name="pageNumber"></param>
    /// <param name="pageSize"></param>
    /// <param name="predicate"></param>
    /// <param name="orderBy"></param>
    /// <param name="includes"></param>
    /// <returns></returns>
    Task<(IEnumerable<T> Items, int TotalCount)> GetPagedAsync(
        int pageNumber,
        int pageSize,
        Expression<Func<T, bool>>? predicate = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null,
        params Expression<Func<T, object>>[] includes);

    /// <summary>
    /// Gets a paginated list of entities using offset-based pagination.
    /// Unlike <see cref="GetPagedAsync"/> which uses page numbers, this method
    /// correctly handles non-page-aligned offsets (e.g., offset=75, limit=50 returns rows 75-124).
    /// </summary>
    /// <param name="offset">Number of rows to skip (0-based)</param>
    /// <param name="limit">Maximum number of rows to return</param>
    /// <param name="predicate">Optional filter predicate</param>
    /// <param name="orderBy">Optional ordering function</param>
    /// <returns>Tuple of matching items and total count</returns>
    Task<(IEnumerable<T> Items, int TotalCount)> GetByOffsetAsync(
        int offset,
        int limit,
        Expression<Func<T, bool>>? predicate = null,
        Func<IQueryable<T>, IOrderedQueryable<T>>? orderBy = null);

    /// <summary>
    /// Gets a queryable for building complex queries
    /// </summary>
    /// <returns>IQueryable of T</returns>
    IQueryable<T> GetQueryable();

    #region Concurrency Handling Methods

    /// <summary>
    /// Saves changes with automatic retry on concurrency conflicts.
    /// </summary>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of affected entities</returns>
    Task<int> SaveChangesWithRetryAsync(
        int maxRetries = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates an entity with automatic retry on concurrency conflicts.
    /// </summary>
    /// <param name="entity">Entity to update</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entity</returns>
    Task<T> UpdateWithRetryAsync(
        T entity,
        int maxRetries = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes an update operation with optimistic concurrency control.
    /// Reloads the entity on each retry attempt.
    /// </summary>
    /// <param name="id">Entity ID</param>
    /// <param name="updateAction">Action to apply updates to the entity</param>
    /// <param name="maxRetries">Maximum number of retry attempts</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Updated entity</returns>
    Task<T?> ExecuteOptimisticUpdateAsync(
        Guid id,
        Action<T> updateAction,
        int maxRetries = 3,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reloads an entity from the database, discarding any local changes.
    /// </summary>
    /// <param name="entity">Entity to reload</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ReloadEntityAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refreshes the context by reloading all tracked entities.
    /// </summary>
    /// <param name="cancellationToken"></param>
    Task RefreshContextAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the change tracker, removing all tracked entities.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task ClearChangeTrackerAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches a single tracked entity from the change tracker without touching any
    /// others. Prefer this over <see cref="ClearChangeTrackerAsync"/> when a caller
    /// needs to drop an entity it just failed to persist but must not disturb entities
    /// tracked by other participants in the same <c>DbContext</c> (for example, an
    /// outer transaction holding audit event rows while an inner integrity write retries).
    /// No-op if the entity is already detached.
    /// </summary>
    /// <param name="entity">Entity to detach.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DetachAsync(T entity, CancellationToken cancellationToken = default);

    /// <summary>
    /// Detaches a sequence of tracked entities from the change tracker. Same contract
    /// as <see cref="DetachAsync"/> but for batch writes that added multiple entities
    /// and need to undo all of them together on failure.
    /// </summary>
    /// <param name="entities">Entities to detach.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task DetachRangeAsync(IEnumerable<T> entities, CancellationToken cancellationToken = default);

    #endregion

    #region Transaction Support

    /// <summary>
    /// Begin transactions
    /// </summary>
    /// <returns></returns>
    Task<IDbContextTransaction> BeginTransactionAsync();

    /// <summary>
    /// Begin Transactions
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Commits the current transaction
    /// </summary>
    Task CommitTransactionAsync();

    /// <summary>
    /// Rolls back the current transaction
    /// </summary>
    Task RollbackTransactionAsync();

    /// <summary>
    /// Current Transaction
    /// </summary>
    IDbContextTransaction? CurrentTransaction { get; }

    /// <summary>
    /// Executes an action within a transaction with automatic rollback on failure.
    /// Uses the configured execution strategy to support retrying execution strategies.
    /// </summary>
    /// <param name="action">The action to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    Task ExecuteInTransactionAsync(
        Func<Task> action,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a function within a transaction with automatic rollback on failure.
    /// Uses the configured execution strategy to support retrying execution strategies.
    /// </summary>
    /// <typeparam name="TResult">The return type</typeparam>
    /// <param name="func">The function to execute</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The result of the function</returns>
    Task<TResult> ExecuteInTransactionAsync<TResult>(
        Func<Task<TResult>> func,
        CancellationToken cancellationToken = default);

    #endregion
}