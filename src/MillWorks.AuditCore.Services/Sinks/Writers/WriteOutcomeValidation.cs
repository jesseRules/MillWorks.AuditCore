using MillWorks.AuditCore.Services.Sinks.Processing;

namespace MillWorks.AuditCore.Services.Sinks.Writers;

internal static class WriteOutcomeValidation
{
    internal static void Validate(IReadOnlyList<WriteOutcome>? outcomes, IEnumerable<Guid> expected)
    {
        var ids = expected.ToHashSet();
        if (outcomes is null || outcomes.Count != ids.Count || outcomes.Any(x => x is null ||
            !ids.Contains(x.EnvelopeId) || (x.Succeeded && (x.IsConflict || x.IsRetryable || x.Exception is not null || x.ErrorMessage is not null)) ||
            (!x.Succeeded && x.IsDuplicate) || (x.IsConflict && (x.IsRetryable || x.Exception is not Identity.AuditIdentityConflictException))) ||
            outcomes.Select(x => x.EnvelopeId).Distinct().Count() != ids.Count)
            throw new AuditBatchContractException("Writer must return exactly one consistent outcome per envelope.");
    }
}
