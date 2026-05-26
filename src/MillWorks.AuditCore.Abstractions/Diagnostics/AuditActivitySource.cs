using System.Diagnostics;

namespace MillWorks.AuditCore.Abstractions.Diagnostics;

/// <summary>
/// Central ActivitySource for MillWorks.AuditCore distributed tracing.
/// <para>
/// Uses only <c>System.Diagnostics</c> (built-in .NET). Zero overhead when no listener is
/// registered — <see cref="ActivitySource.StartActivity(string, ActivityKind)"/> returns null and
/// <c>?.SetTag()</c> is a no-op.
/// </para>
/// <para>
/// Consumers should register the source name with their tracing provider:
/// <code>
/// builder.Services.AddOpenTelemetry()
///     .WithTracing(t => t.AddAuditCoreInstrumentation());
/// </code>
/// </para>
/// </summary>
public static class AuditActivitySource
{
    /// <summary>
    /// The ActivitySource name for consumer registration.
    /// </summary>
    public const string Name = "MillWorks.AuditCore";

    /// <summary>
    /// The ActivitySource version.
    /// </summary>
    public const string Version = "1.0.0";

    /// <summary>
    /// The shared ActivitySource instance for all audit spans.
    /// </summary>
    public static readonly ActivitySource Source = new(Name, Version);

    /// <summary>
    /// Operation name constants for <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>.
    /// </summary>
    public static class Operations
    {
        /// <summary>
        /// A single audit event is being written.
        /// </summary>
        public const string AuditWrite = "audit.write";

        /// <summary>
        /// A batch of audit events is being written.
        /// </summary>
        public const string AuditWriteBatch = "audit.write_batch";

        /// <summary>
        /// An audit query is being executed.
        /// </summary>
        public const string AuditQuery = "audit.query";

        /// <summary>
        /// Audit events are being archived to blob storage.
        /// </summary>
        public const string AuditArchive = "audit.archive";

        /// <summary>
        /// Archived audit events are being restored.
        /// </summary>
        public const string AuditRestore = "audit.restore";

        /// <summary>
        /// An outbox row is being written.
        /// </summary>
        public const string OutboxWrite = "outbox.write";

        /// <summary>
        /// A batch of outbox rows is being drained.
        /// </summary>
        public const string OutboxDrain = "outbox.drain";

        /// <summary>
        /// An integrity record is being created.
        /// </summary>
        public const string IntegrityWrite = "integrity.write";

        /// <summary>
        /// A batch of integrity records is being flushed.
        /// </summary>
        public const string IntegrityFlush = "integrity.flush";

        /// <summary>
        /// An integrity check is being performed.
        /// </summary>
        public const string IntegrityCheck = "integrity.check";

        /// <summary>
        /// Integrity reconciliation is running.
        /// </summary>
        public const string IntegrityReconcile = "integrity.reconcile";
    }

    /// <summary>
    /// Tag key constants for <see cref="Activity.SetTag"/>.
    /// </summary>
    public static class Tags
    {
        /// <summary>
        /// Tag key for the audit event identifier.
        /// </summary>
        public const string AuditEventId = "audit.event.id";

        /// <summary>
        /// Tag key for the audit event type.
        /// </summary>
        public const string AuditEventType = "audit.event.type";

        /// <summary>
        /// Tag key for the audited entity type.
        /// </summary>
        public const string AuditEntityType = "audit.entity.type";

        /// <summary>
        /// Tag key for the audited entity identifier.
        /// </summary>
        public const string AuditEntityId = "audit.entity.id";

        /// <summary>
        /// Tag key for the user identifier associated with the audit event.
        /// </summary>
        public const string AuditUserId = "audit.user.id";

        /// <summary>
        /// Tag key for the number of items in a batch operation.
        /// </summary>
        public const string BatchSize = "batch.size";

        /// <summary>
        /// Tag key for the number of items processed.
        /// </summary>
        public const string ProcessedCount = "processed.count";

        /// <summary>
        /// Generic outcome tag for operations that succeed or fail.
        /// </summary>
        public const string Outcome = "outcome";

        /// <summary>
        /// Tag key for the archive identifier.
        /// </summary>
        public const string ArchiveId = "archive.id";

        /// <summary>
        /// Tag key for the query type being executed.
        /// </summary>
        public const string QueryType = "query.type";

        /// <summary>
        /// Tag key for retry attempt number.
        /// </summary>
        public const string RetryAttempt = "retry.attempt";

        /// <summary>
        /// Tag key for the outbox row identifier.
        /// </summary>
        public const string OutboxRowId = "outbox.row.id";
    }

    /// <summary>
    /// ActivityEvent name constants for <see cref="Activity.AddEvent"/>.
    /// </summary>
    public static class Events
    {
        /// <summary>
        /// An audit event was routed to the dead letter queue.
        /// </summary>
        public const string DlqRouted = "dlq_routed";

        /// <summary>
        /// An integrity verification failed.
        /// </summary>
        public const string IntegrityFailed = "integrity_failed";

        /// <summary>
        /// A retry attempt is being made.
        /// </summary>
        public const string RetryAttempt = "retry_attempt";

        /// <summary>
        /// An outbox row exhausted its retry attempts.
        /// </summary>
        public const string OutboxExhausted = "outbox_exhausted";
    }
}
