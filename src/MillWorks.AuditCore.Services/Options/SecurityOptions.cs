namespace MillWorks.AuditCore.Services.Database.Options;

/// <summary>
/// Security options for audit logging
/// </summary>
public sealed class SecurityOptions
{
    /// <summary>
    /// Enable tamper detection for audit logs
    /// </summary>
    public bool EnableTamperDetection { get; set; } = true;

    /// <summary>
    /// Use Redis for distributed locking
    /// </summary>
    public bool UseRedisLocking { get; set; } = false;

    /// <summary>
    /// Redis connection string for distributed locking
    /// </summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>
    /// Enable digital signatures for audit events
    /// </summary>
    public bool EnableDigitalSignatures { get; set; } = false;

    /// <summary>
    /// Path to the private key for signing audit events
    /// </summary>
    public string? PrivateKeyPath { get; set; }

    /// <summary>
    /// Path to the public key for verifying audit event signatures
    /// </summary>
    public string? PublicKeyPath { get; set; }

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
