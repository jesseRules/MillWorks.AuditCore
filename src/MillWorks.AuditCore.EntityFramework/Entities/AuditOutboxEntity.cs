using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace MillWorks.AuditCore.EntityFramework.Entities;

/// <summary>
/// Transactional outbox row for audit envelope handoff. Written by
/// <c>TransactionalOutboxSink</c> inside the consumer's transaction,
/// drained by <c>AuditOutboxDrainer</c> to the audit DbContext after commit.
/// </summary>
[Table("AuditOutbox", Schema = "audit")]
public sealed class AuditOutboxEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Serialized <see cref="MillWorks.AuditCore.Abstractions.Models.AuditEnvelope"/>
    /// in JSON format. The drainer deserializes and forwards to
    /// <c>ImmediateSink.PublishAsync</c>.
    /// </summary>
    [Required]
    public string EnvelopeJson { get; set; } = string.Empty;

    /// <summary>
    /// Format version of the serialized envelope. Allows the drainer to
    /// detect version skew and fail loud rather than silently mis-parsing.
    /// Increment when the envelope schema changes incompatibly.
    /// </summary>
    [Required]
    public int EnvelopeVersion { get; set; } = 1;

    [Required]
    public AuditOutboxStatus Status { get; set; } = AuditOutboxStatus.Pending;

    [Required]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Earliest time this row may be retried. Null means immediately eligible.
    /// Set on failure to enforce exponential backoff between attempts.
    /// </summary>
    public DateTimeOffset? NextRetryAt { get; set; }

    public int AttemptCount { get; set; }

    [MaxLength(2000)]
    public string? LastError { get; set; }

    /// <summary>
    /// Idempotency key for duplicate envelope detection. Derived from envelope identity:
    /// <list type="bullet">
    /// <item><description>ExplicitEvent: <c>AuditEvent.EventId</c> from the explicit event payload</description></item>
    /// <item><description>EntityChange: <c>AuditEnvelope.EnvelopeId</c> (stable envelope identity)</description></item>
    /// </list>
    /// Unique constraint prevents duplicate outbox rows for the same logical envelope.
    /// </summary>
    [Required]
    public Guid IdempotencyKey { get; set; }

    /// <summary>
    /// Unique identifier of the drainer instance that claimed this row for processing.
    /// Format: <c>{hostname}:{pid}:{guid-suffix}</c>. Null when row is not claimed.
    /// </summary>
    [MaxLength(100)]
    public string? LeaseOwner { get; set; }

    /// <summary>
    /// When the current lease expires. After this time, another drainer may reclaim
    /// the row if the original drainer crashed or is unresponsive.
    /// </summary>
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}

/// <summary>
/// Lifecycle state of an outbox row.
/// </summary>
public enum AuditOutboxStatus
{
    /// <summary>Row is awaiting drain to the audit DbContext.</summary>
    Pending = 0,

    /// <summary>Row is claimed by a drainer and being processed.</summary>
    InFlight = 1,

    /// <summary>Drainer successfully published the envelope.</summary>
    Completed = 2,

    /// <summary>
    /// Drainer exhausted retries. Envelope routed to DLQ; business row
    /// persisted (soft-failure mode of outbox pattern).
    /// </summary>
    Failed = 3,
}
