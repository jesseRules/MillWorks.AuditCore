using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Library-facing interface for publishing audit events. Implementations
/// route envelopes to the configured <see cref="IAuditSink"/>.
/// </summary>
/// <remarks>
/// Libraries inject this interface to publish explicit audit events.
/// The bridge layer (MillWorks.Api) implements this interface and
/// delegates to the underlying sink, which handles persistence semantics
/// (immediate commit, transactional outbox, etc.).
/// </remarks>
public interface IAuditPublisher
{
    /// <summary>
    /// Publish an audit envelope.
    /// </summary>
    /// <param name="envelope">The envelope to publish.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default);
}
