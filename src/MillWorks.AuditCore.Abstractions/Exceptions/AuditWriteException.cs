using MillWorks.AuditCore.Abstractions.Enums;

namespace MillWorks.AuditCore.Abstractions.Exceptions;

/// <summary>
/// Thrown when audit envelope writes fail in immediate sink mode. Since immediate mode
/// has no outbox durability fallback, write failures must propagate to callers so they
/// can decide whether to abort or continue.
/// </summary>
public sealed class AuditWriteException : Exception
{
    /// <summary>
    /// Total envelopes in the batch that was attempted.
    /// </summary>
    public int TotalCount { get; }

    /// <summary>
    /// Number of envelopes that failed to write.
    /// </summary>
    public int FailedCount { get; }

    /// <summary>
    /// Envelope IDs that failed, for correlation with retry logic.
    /// </summary>
    public IReadOnlyList<Guid> FailedEnvelopeIds { get; }

    /// <summary>
    /// The envelope kind being written when the failure occurred.
    /// </summary>
    public AuditEnvelopeKind? Kind { get; }

    public AuditWriteException(
        int totalCount,
        int failedCount,
        IReadOnlyList<Guid> failedEnvelopeIds,
        AuditEnvelopeKind? kind,
        string firstError,
        Exception? innerException = null)
        : base(FormatMessage(totalCount, failedCount, kind, firstError), innerException)
    {
        TotalCount = totalCount;
        FailedCount = failedCount;
        FailedEnvelopeIds = failedEnvelopeIds;
        Kind = kind;
    }

    private static string FormatMessage(int totalCount, int failedCount, AuditEnvelopeKind? kind, string firstError)
    {
        var kindStr = kind?.ToString() ?? "mixed";
        return $"Audit write failed: {failedCount}/{totalCount} {kindStr} envelope(s) failed. First error: {firstError}";
    }
}
