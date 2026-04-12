namespace MillWorks.AuditCore.Services.Options;

/// <summary>
/// Configuration for request-level HTTP audit middleware behavior.
/// </summary>
public sealed class AuditMiddlewareOptions
{
    /// <summary>
    /// Additional path prefixes to exclude from request-level audit dispatching on GET/HEAD requests.
    /// </summary>
    public List<string> ExcludedReadPaths { get; set; } = [];

    /// <summary>
    /// When true, only POST/PUT/PATCH/DELETE requests are dispatched as request-level audits.
    /// </summary>
    public bool AuditWritesOnly { get; set; }

    /// <summary>
    /// Maximum number of deferred request-audit events buffered in the default in-process queue.
    /// </summary>
    public int QueueCapacity { get; set; } = 1000;

    /// <summary>
    /// Maximum time the request thread will wait to enqueue a deferred request audit.
    /// Use <see cref="TimeSpan.Zero"/> to fail fast when the queue is full.
    /// </summary>
    public TimeSpan EnqueueTimeout { get; set; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Maximum time the default in-process worker will spend draining queued request audits during shutdown.
    /// </summary>
    public TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
