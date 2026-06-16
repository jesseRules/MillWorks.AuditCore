using Microsoft.Extensions.Options;
using MillWorks.AuditCore.Abstractions.Dto;

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

    // ─── Phase 06: Transactional outbox sink + background drainer ───────────
    // AuditSinkMode selects the active sink at runtime; every OutboxDrainer*
    // property governs the background drainer and only applies when
    // AuditSinkMode == TransactionalOutbox.

    /// <summary>
    /// Selects which IAuditSink implementation handles audit envelopes at
    /// runtime. Default is Immediate (audit row commits independently of the
    /// consumer's transaction). TransactionalOutbox is the opt-in posture for
    /// regulated / zero-loss-durability deployments where audit + business
    /// must succeed atomically.
    /// </summary>
    public AuditSinkMode AuditSinkMode { get; set; } = AuditSinkMode.Immediate;

    /// <summary>
    /// Polling interval for the outbox drainer. Default 250ms.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan OutboxDrainerPollInterval { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Maximum number of pending outbox rows the drainer reads per cycle. Default 100.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public int OutboxDrainerBatchSize { get; set; } = 100;

    /// <summary>
    /// Per-row retry backoff schedule when an outbox row's drain attempt fails.
    /// Default: 1s, 5s, 30s. The drainer applies the next entry after each failed
    /// attempt; the schedule's length together with <see cref="OutboxDrainerMaxAttempts"/>
    /// caps total per-row retries before the row is marked Failed and the envelope
    /// is routed to the dead-letter queue.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan[] OutboxDrainerRetryBackoff { get; set; } = new[]
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Jitter ratio applied to each backoff step to prevent thundering-herd retry
    /// across replicas. Default 0.2 (±20%). Range: [0.0, 1.0]; 0 disables jitter.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public double OutboxDrainerBackoffJitterRatio { get; set; } = 0.2;

    /// <summary>
    /// Maximum drain attempts per outbox row before the row is marked Failed and
    /// the envelope is routed to the dead-letter queue. Default 5. Range: >= 1.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public int OutboxDrainerMaxAttempts { get; set; } = 5;

    /// <summary>
    /// Number of consecutive drain-cycle failures (e.g. audit DB unreachable) before
    /// the drainer enters the circuit-breaker open state and sleeps for
    /// <see cref="OutboxDrainerCircuitBreakerSleep"/>. Default 5. Range: >= 1.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public int OutboxDrainerCircuitBreakerThreshold { get; set; } = 5;

    /// <summary>
    /// Duration the drainer sleeps when the circuit breaker is open. Default 60s.
    /// On wakeup the drainer attempts one cycle; success closes the breaker, failure
    /// re-opens it for another sleep.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan OutboxDrainerCircuitBreakerSleep { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Duration of the lease acquired when a drainer claims outbox rows for processing.
    /// If the drainer crashes or takes longer than this duration, another drainer may
    /// reclaim the rows. Default 60s. Should be longer than expected processing time
    /// for a single batch.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan OutboxDrainerLeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between lease recovery sweeps. The drainer periodically scans for
    /// InFlight rows with expired leases (crashed drainer) and resets them to Pending
    /// for reprocessing. Default 5 minutes.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan OutboxDrainerLeaseRecoveryInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Interval between outbox queue-depth samples that feed the observability gauges
    /// (<c>audit.outbox.pending_count</c>, <c>audit.outbox.inflight_count</c>,
    /// <c>audit.outbox.oldest_pending_age_seconds</c>). The drainer runs three cheap,
    /// index-backed aggregate queries per sample, so this is decoupled from the (typically
    /// sub-second) poll interval to bound database load. Default 10s.
    /// Only applies under TransactionalOutbox sink mode.
    /// </summary>
    public TimeSpan OutboxQueueDepthSampleInterval { get; set; } = TimeSpan.FromSeconds(10);
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

        if (options.AuditSinkMode == AuditSinkMode.TransactionalOutbox)
        {
            if (options.OutboxDrainerPollInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerPollInterval)} must be > 0 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerBatchSize < 1)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerBatchSize)} must be >= 1 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerRetryBackoff is null || options.OutboxDrainerRetryBackoff.Length == 0)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerRetryBackoff)} must contain at least one entry when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }
            else
            {
                foreach (var step in options.OutboxDrainerRetryBackoff)
                {
                    if (step <= TimeSpan.Zero)
                    {
                        failures.Add(
                            $"All entries in {nameof(SecurityOptions.OutboxDrainerRetryBackoff)} must be > 0.");
                        break;
                    }
                }
            }

            if (options.OutboxDrainerBackoffJitterRatio is < 0.0 or > 1.0)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerBackoffJitterRatio)} must be in [0.0, 1.0].");
            }

            if (options.OutboxDrainerMaxAttempts < 1)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerMaxAttempts)} must be >= 1 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerCircuitBreakerThreshold < 1)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerCircuitBreakerThreshold)} must be >= 1 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerCircuitBreakerSleep <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerCircuitBreakerSleep)} must be > 0 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerLeaseDuration <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerLeaseDuration)} must be > 0 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxDrainerLeaseRecoveryInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxDrainerLeaseRecoveryInterval)} must be > 0 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }

            if (options.OutboxQueueDepthSampleInterval <= TimeSpan.Zero)
            {
                failures.Add(
                    $"{nameof(SecurityOptions.OutboxQueueDepthSampleInterval)} must be > 0 when " +
                    $"{nameof(SecurityOptions.AuditSinkMode)} is {nameof(AuditSinkMode.TransactionalOutbox)}.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
