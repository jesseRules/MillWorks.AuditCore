namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Implemented by a <c>DbContext</c> (or other context-bearing object) to expose
/// request-scoped context to the audit pipeline. The interceptor reads these
/// properties when building <see cref="Models.AuditEnvelope"/> instances; the
/// values flow through to <see cref="Models.AuditEnvelopePropertyChange"/>-bearing
/// rows and explicit-event payloads as <c>UserId</c> / <c>CorrelationId</c> /
/// <c>IpAddress</c> / <c>UserAgent</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this interface exists.</b> Before this contract, the interceptor cast
/// the saving <c>DbContext</c> to <c>AuditDbContext</c> to read these
/// fields. That coupled every audited <c>DbContext</c> to a specific AuditCore
/// type and prevented consumer libraries from referencing only
/// <c>MillWorks.AuditCore.Abstractions</c>. Implementing
/// <see cref="IAuditContextSource"/> is the supported way for a consumer
/// <c>DbContext</c> to feed user / correlation / IP / user-agent context into
/// audit envelopes without depending on the EntityFramework package.
/// </para>
/// <para>
/// <b>Read-only by design.</b> The interface exposes getters only. How values
/// are <em>set</em> is an implementation concern: middleware can write to
/// settable properties, claims-principal-derived implementations can compute
/// the value on every read, and DI-resolved implementations can pull from
/// <c>IHttpContextAccessor</c>. Constraining the interface to readers keeps the
/// public contract minimal — a setter shape that turns out wrong is a breaking
/// change to remove, while adding setters later (via a sibling
/// mutable-context-source interface) is non-breaking.
/// </para>
/// <para>
/// <b>Null semantics.</b> All four properties are nullable. Implementations
/// must return <c>null</c> when the value is genuinely unavailable — for
/// example, background work outside an HTTP request, or before middleware has
/// populated the context for the current scope. The audit pipeline tolerates
/// nulls everywhere downstream; nothing throws on a missing field.
/// </para>
/// <para>
/// <b>Threading.</b> The implementation is expected to follow the
/// single-threaded read-then-write-per-request-scope pattern that EF Core
/// itself enforces on <c>DbContext</c>. Concurrent reads from multiple threads
/// against a single instance are <em>not</em> supported by this contract;
/// implementations are not required to add synchronization, and the interceptor
/// only reads from the saving thread. Callers that share a context across
/// threads (which EF Core itself discourages) are responsible for their own
/// synchronization.
/// </para>
/// </remarks>
public interface IAuditContextSource
{
    /// <summary>
    /// Identifier of the user whose action is being audited, when known.
    /// Typically populated by ASP.NET middleware from the authenticated principal.
    /// </summary>
    string? CurrentUserId { get; }

    /// <summary>
    /// Correlation identifier linking related events across systems for the
    /// current request scope, when known.
    /// </summary>
    string? CurrentCorrelationId { get; }

    /// <summary>
    /// IP address of the client that triggered the audited operation, when known.
    /// </summary>
    string? CurrentIpAddress { get; }

    /// <summary>
    /// User-agent string of the client that triggered the audited operation,
    /// when known.
    /// </summary>
    string? CurrentUserAgent { get; }
}
