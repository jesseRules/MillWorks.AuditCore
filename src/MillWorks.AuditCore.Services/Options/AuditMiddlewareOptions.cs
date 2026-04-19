using Microsoft.Extensions.Options;

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

/// <summary>
/// Runtime validator for <see cref="AuditMiddlewareOptions"/>. Registered via the options
/// pipeline with <c>ValidateOnStart()</c> so misconfiguration fails at host boot.
/// </summary>
internal sealed class AuditMiddlewareOptionsValidator : IValidateOptions<AuditMiddlewareOptions>
{
    public ValidateOptionsResult Validate(string? name, AuditMiddlewareOptions options)
    {
        var failures = new List<string>();

        if (options.QueueCapacity <= 0)
        {
            failures.Add(
                $"{nameof(AuditMiddlewareOptions.QueueCapacity)} must be > 0.");
        }

        if (options.EnqueueTimeout < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(AuditMiddlewareOptions.EnqueueTimeout)} must be >= TimeSpan.Zero.");
        }

        if (options.DrainTimeout < TimeSpan.Zero)
        {
            failures.Add(
                $"{nameof(AuditMiddlewareOptions.DrainTimeout)} must be >= TimeSpan.Zero.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
