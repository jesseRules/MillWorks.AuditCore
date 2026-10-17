using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Services.Sinks;

namespace MillWorks.AuditCore.Tests.Helpers;

// Explicit authority for trusted, isolated test producers; never registered by production code.
internal sealed class TestAuditWriteAuthorization : IAuditEnvelopeWriteAuthorization
{
    internal static TestAuditWriteAuthorization Instance { get; } = new();
    public ValueTask<IAuditEnvelopeWritePermit> AcquireAsync(IReadOnlyList<AuditEnvelope> envelopes, CancellationToken ct)
        => ValueTask.FromResult<IAuditEnvelopeWritePermit>(new Permit());
    private sealed class Permit : IAuditEnvelopeWritePermit
    {
        public ValueTask CheckCurrentAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
