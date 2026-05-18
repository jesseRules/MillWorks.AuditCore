using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.EntityFramework.Sinks;

/// <summary>
/// Provides access to the consumer's DbContext during audit sink operations.
/// When <c>AuditSinkMode.TransactionalOutbox</c> is active, the outbox writer
/// uses this to participate in the consumer's transaction. When <c>Immediate</c>
/// mode is active, the accessor is populated but ignored.
/// </summary>
/// <remarks>
/// <para>
/// The interceptor populates this accessor via <see cref="SetCurrent"/> before
/// calling <c>IAuditSink.PublishAsync</c>, then disposes the scope afterward.
/// </para>
/// <para>
/// <b>Fail-closed behavior (D1):</b> Reading <see cref="Current"/> when no
/// context is in scope throws <see cref="InvalidOperationException"/>. There
/// is no silent fallback — outbox mode requires an active consumer DbContext.
/// </para>
/// </remarks>
public interface IConsumerDbContextAccessor
{
    /// <summary>
    /// Gets the current consumer DbContext. Throws if not set.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when accessed outside of a <see cref="SetCurrent"/> scope.
    /// </exception>
    DbContext Current { get; }

    /// <summary>
    /// Returns true if a consumer DbContext is currently set, false otherwise.
    /// Use this to check availability without throwing.
    /// </summary>
    bool HasCurrent { get; }

    /// <summary>
    /// Sets the current consumer DbContext for the duration of the returned scope.
    /// Dispose the scope to clear the context.
    /// </summary>
    /// <param name="context">The consumer's DbContext instance.</param>
    /// <returns>An <see cref="IDisposable"/> that clears the context on dispose.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when called while a context is already set (nesting not allowed).
    /// </exception>
    IDisposable SetCurrent(DbContext context);
}
