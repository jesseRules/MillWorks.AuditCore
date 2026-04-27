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

    private void Clear() => _current = null;

    private sealed class ContextScope(ConsumerDbContextAccessor accessor) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            accessor.Clear();
        }
    }
}
