using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Interfaces;

/// <summary>
/// Required capability for loggers used by the explicit-envelope writer. Implementations
/// must atomically persist canonical identity evidence with events, verify content on replay,
/// and reject conflicting/unverifiable reuse. Aggregate-only legacy loggers cannot safely
/// supply this protocol. EventOutcomes is mandatory: exactly one recognized result for each
/// distinct input ExplicitEventId, including failures, consistent with aggregate fields. The publishing host authorizes the operation before calling a sink;
/// this capability never returns stored payload, tenant or comparison evidence.
/// </summary>
public interface IAuditEnvelopeBatchLogger
{
    Task<BatchAuditResult> LogEnvelopeBatchAsync(IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken = default);
}
