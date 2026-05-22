using Microsoft.EntityFrameworkCore;

namespace MillWorks.AuditCore.EntityFramework.Sinks;

/// <summary>
/// Scoped accessor that holds the consumer's DbContext during audit sink operations.
/// </summary>
/// <remarks>
/// Registered as scoped. The interceptor sets the context before calling the sink,
/// and disposes the scope after the publish completes. This ensures each SaveChanges
/// operation gets an isolated accessor instance.
/// </remarks>
internal sealed class ConsumerDbContextAccessor : IConsumerDbContextAccessor
{
    /// <summary>
    /// The current consumer DbContext. Set by the interceptor before sink invocation.
    /// </summary>
    private DbContext? _current;

    /// <inheritdoc />
    public DbContext Current =>
        _current ?? throw new InvalidOperationException(
            "No consumer DbContext is currently set. This accessor must be populated " +
            "by the AuditSaveChangesInterceptor before the audit sink is invoked. " +
            "If you are using AuditSinkMode.TransactionalOutbox, ensure the interceptor " +
            "is registered and the save operation is going through an intercepted DbContext.");

    /// <inheritdoc />
    public bool HasCurrent => _current is not null;

    /// <inheritdoc />
    public IDisposable SetCurrent(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (_current is not null)
        {
            throw new InvalidOperationException(
                "A consumer DbContext is already set. Nested SetCurrent calls are not allowed. " +
                "This indicates a bug in the interceptor or concurrent access to a scoped accessor.");
        }

        _current = context;
        return new ContextScope(this);
    }

    /// <summary>
    /// Clears the current DbContext. Called by the ContextScope on dispose to ensure proper cleanup.
    /// </summary>
    private void Clear() => _current = null;

    /// <summary>
    /// Private scope class that clears the current DbContext on dispose.
    /// This is returned by SetCurrent to ensure proper cleanup after the sink operation completes.
    /// </summary>
    /// <param name="accessor"></param>
    private sealed class ContextScope(ConsumerDbContextAccessor accessor) : IDisposable
    {
        /// <summary>
        /// Flag to ensure Dispose is idempotent. The interceptor should only dispose once, but this guards against misuse or multiple disposals.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Disposes the scope, clearing the current DbContext from the accessor.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            accessor.Clear();
        }
    }
}
