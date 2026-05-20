using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Models;

/// <summary>
/// Subsystem-neutral payload published to <see cref="MillWorks.AuditCore.Abstractions.Interfaces.IAuditSink"/>.
/// Carries everything a sink needs to persist an audit row — entity-change diffs,
/// explicit-event payload, or both — without referencing any EF or persistence type.
/// </summary>
/// <remarks>
/// <para>
/// The envelope is immutable after construction. Producers (the
/// <c>AuditSaveChangesInterceptor</c> and <c>IAuditLogger</c> callers) build it
/// once; the sink consumes it once.
/// </para>
/// <para>
/// Field population is keyed by <see cref="Kind"/>:
/// <list type="bullet">
/// <item><description><see cref="AuditEnvelopeKind.EntityChange"/> — populates
/// <see cref="PropertyChanges"/>; <see cref="EventType"/> and
/// <see cref="AdditionalData"/> are typically null.</description></item>
/// <item><description><see cref="AuditEnvelopeKind.ExplicitEvent"/> — populates
/// <see cref="EventType"/> and optionally <see cref="AdditionalData"/>;
/// <see cref="PropertyChanges"/> is typically null.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed record AuditEnvelope
{
    /// <summary>
    /// Stable identity for result correlation. This is NOT the same thing as an
    /// explicit event's AuditEvent.EventId and must exist for EntityChange envelopes too.
    /// Producers must preserve EnvelopeId across retries and outbox serialization.
    /// </summary>
    public Guid EnvelopeId { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Discriminator identifying the producer path and which optional fields are populated.
    /// </summary>
    public required AuditEnvelopeKind Kind { get; init; }

    /// <summary>
    /// Name of the entity or subject being audited (e.g., <c>"Patient"</c>,
    /// <c>"User.Login"</c>). Required.
    /// </summary>
    public required string EntityName { get; init; }

    /// <summary>
    /// The audit action being performed.
    /// </summary>
    public required AuditAction Action { get; init; }

    /// <summary>
    /// Primary key of the affected entity, when known and representable as a Guid.
    /// </summary>
    public Guid? EntityId { get; init; }

    /// <summary>
    /// User identifier supplied by the producer (typically the caller's user-id string).
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Correlation identifier linking related events across systems.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// IP address of the client that triggered the audited operation.
    /// </summary>
    public string? IpAddress { get; init; }

    /// <summary>
    /// User agent string of the client that triggered the audited operation.
    /// </summary>
    public string? UserAgent { get; init; }

    /// <summary>
    /// UTC timestamp when the audited event occurred. Defaults to construction time.
    /// </summary>
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Per-property diff list. Populated when <see cref="Kind"/> is
    /// <see cref="AuditEnvelopeKind.EntityChange"/>. May be null for
    /// Added/Deleted entries that do not carry per-property diffs.
    /// </summary>
    public IReadOnlyList<AuditEnvelopePropertyChange>? PropertyChanges { get; init; }

    /// <summary>
    /// Event type string (e.g., <c>"User.Login"</c>). Populated when
    /// <see cref="Kind"/> is <see cref="AuditEnvelopeKind.ExplicitEvent"/>.
    /// </summary>
    public string? EventType { get; init; }

    /// <summary>
    /// Optional serialized payload that accompanies an
    /// <see cref="AuditEnvelopeKind.ExplicitEvent"/>.
    /// </summary>
    public string? AdditionalData { get; init; }

    /// <summary>
    /// Optional human-readable description.
    /// </summary>
    public string? Description { get; init; }
}
