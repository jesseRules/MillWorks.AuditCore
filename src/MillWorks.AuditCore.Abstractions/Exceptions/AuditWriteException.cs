using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown when audit envelope writes fail in immediate sink mode. Since immediate mode
/// has no outbox durability fallback, write failures must propagate to callers so they
/// can decide whether to abort or continue.
/// </summary>
public sealed class AuditWriteException(
    int totalCount,
    int failedCount,
    IReadOnlyList<Guid> failedEnvelopeIds,
    AuditEnvelopeKind? kind,
    string firstError,
    Exception? innerException = null)
    : Exception(FormatMessage(totalCount, failedCount, kind, firstError), innerException)
{
    /// <summary>
    /// Total envelopes in the batch that was attempted.
    /// </summary>
    public int TotalCount { get; } = totalCount;

    /// <summary>
    /// Number of envelopes that failed to write.
    /// </summary>
    public int FailedCount { get; } = failedCount;

    /// <summary>
    /// Envelope IDs that failed, for correlation with retry logic.
    /// </summary>
    public IReadOnlyList<Guid> FailedEnvelopeIds { get; } = failedEnvelopeIds;

    /// <summary>
    /// The envelope kind being written when the failure occurred.
    /// </summary>
    public AuditEnvelopeKind? Kind { get; } = kind;

    /// <summary>
    /// Short human-readable description of the first error encountered (e.g., exception message from the sink).
    /// </summary>
    /// <param name="totalCount"></param>
    /// <param name="failedCount"></param>
    /// <param name="kind"></param>
    /// <param name="firstError"></param>
    /// <returns></returns>
    private static string FormatMessage(int totalCount, int failedCount, AuditEnvelopeKind? kind, string firstError)
    {
        var kindStr = kind?.ToString() ?? "mixed";
        return $"Audit write failed: {failedCount}/{totalCount} {kindStr} envelope(s) failed. First error: {firstError}";
    }
}
