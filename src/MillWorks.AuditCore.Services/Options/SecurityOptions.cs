using Microsoft.Extensions.Options;

namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Security options for audit logging. Owns tamper-detection, distributed-locking,
/// digital-signature key paths, and batched integrity settings.
/// HMAC key and digital-signature enablement are owned by
/// <c>MillWorks.AuditCore.Services.Options.AuditOptions</c>.
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Enable tamper detection for audit logs
    /// </summary>
    public bool EnableTamperDetection { get; set; } = true;

    /// <summary>
    /// Use Redis for the general-purpose <c>IAuditDistributedLockService</c>. Does NOT govern
    /// integrity-chain append correctness — that is serialized by SQL Server
    /// <c>sp_getapplock</c> inside the write transaction (see
    /// <c>IAuditIntegrityRepository.AcquireAppendLockAsync</c>) regardless of this setting.
    /// This flag only affects callers that use the shared lock service for other coordination
    /// (e.g. the dead-letter-queue leader election).
    /// <para>
    /// When true, the consuming application must register an <c>IConnectionMultiplexer</c>
    /// in the service collection (e.g.
    /// <c>services.AddSingleton&lt;IConnectionMultiplexer&gt;(_ =&gt; ConnectionMultiplexer.Connect("..."))</c>)
    /// before the audit distributed lock service is resolved. AuditCore does not own the
    /// <c>IConnectionMultiplexer</c> registration — consuming apps typically already register
    /// one for their own components (token caches, rate limiters, etc.).
    /// </para>
    /// </summary>
    public bool UseRedisLocking { get; set; } = false;

    /// <summary>
    /// Path to the private key PEM file for signing audit events.
    /// Used by tamper detection when <c>AuditOptions.EnableDigitalSignatures</c> is true.
    /// </summary>
    public string? DigitalSignaturePrivateKeyPath { get; set; }

    /// <summary>
    /// Path to the public key PEM file for verifying audit event signatures.
    /// Used by tamper detection when <c>AuditOptions.EnableDigitalSignatures</c> is true.
    /// </summary>
    public string? DigitalSignaturePublicKeyPath { get; set; }

    /// <summary>
    /// When true, integrity record writes are deferred to a bounded background worker
    /// that flushes in short batches. This reduces per-event lock contention and database
    /// round-trips under high concurrency. The worker guarantees flush-on-shutdown.
    /// Default: false (synchronous immediate writes — safest, lowest throughput).
    /// </summary>
    public bool EnableBatchedIntegrityWrites { get; set; }

    /// <summary>
    /// Maximum number of integrity records to accumulate before the background worker
    /// flushes a batch. Only applies when <see cref="EnableBatchedIntegrityWrites"/> is true.
    /// Default: 50.
    /// </summary>
    public int IntegrityBatchSize { get; set; } = 50;

    /// <summary>
    /// Maximum time the background worker will wait before flushing a partial batch.
    /// Only applies when <see cref="EnableBatchedIntegrityWrites"/> is true.
    /// Default: 500ms.
    /// </summary>
    public TimeSpan IntegrityFlushInterval { get; set; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// Runtime validator for <see cref="SecurityOptions"/>. Registered via the options pipeline
/// with <c>ValidateOnStart()</c> so misconfiguration fails at host boot, not at first use.
/// </summary>
internal sealed class SecurityOptionsValidator : IValidateOptions<SecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, SecurityOptions options)
    {
        var failures = new List<string>();

        if (options.EnableBatchedIntegrityWrites)
        {
            if (options.IntegrityBatchSize < 1)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.IntegrityBatchSize)} must be >= 1 when " +
                    $"{nameof(SecurityOptions.EnableBatchedIntegrityWrites)} is true.");
            }

            if (options.IntegrityFlushInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.IntegrityFlushInterval)} must be > 0 when " +
                    $"{nameof(SecurityOptions.EnableBatchedIntegrityWrites)} is true.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
