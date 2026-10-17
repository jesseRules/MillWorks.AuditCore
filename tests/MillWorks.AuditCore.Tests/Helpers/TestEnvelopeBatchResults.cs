using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks.Writers;

namespace MillWorks.AuditCore.Tests.Helpers;

// Explicit mock fixture construction only; production loggers must observe each write.
internal static class TestEnvelopeBatchResults
{
    internal static BatchAuditResult Create(IReadOnlyList<AuditEnvelope> inputs, BatchAuditResult fixture)
    {
        var failed = inputs.Where(e => !fixture.Success && (fixture.FailedEvents.Count == 0 ||
            fixture.FailedEvents.Any(f => f.EventId == e.ExplicitEventId))).ToArray();
        return new BatchAuditResult
        {
            Success = failed.Length == 0, EventCount = inputs.Count, IsDuplicate = fixture.IsDuplicate,
            FailedEvents = failed.Select(AuditEventBatchWriter.MapToAuditEvent).ToArray(), Exception = fixture.Exception,
            EventOutcomes = inputs.Select(e => new AuditEventOutcome(e.ExplicitEventId!.Value,
                failed.Contains(e) ? AuditEventWriteStatus.Failed : fixture.IsDuplicate ? AuditEventWriteStatus.Duplicate : AuditEventWriteStatus.Persisted,
                failed.Contains(e) ? fixture.Exception : null, failed.Contains(e))).ToArray()
        };
    }
}
