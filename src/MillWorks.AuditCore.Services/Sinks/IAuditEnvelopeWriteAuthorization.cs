using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks;

/// <summary>Host-owned current authority for publishing and classifying replay/conflict.
/// Derive actor/tenant/resource rights from the trusted caller, not from submitted metadata.
/// Authorize every envelope, including an explicit quarantine scope for absent tenant metadata.
/// No permissive default is registered. Hold authority through sink completion; for outbox
/// staging the host also owns permission/transaction coordination through consumer commit.</summary>
public interface IAuditEnvelopeWriteAuthorization
{
    ValueTask<IAuditEnvelopeWritePermit> AcquireAsync(IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken);
}

public interface IAuditEnvelopeWritePermit : IAsyncDisposable
{
    ValueTask CheckCurrentAsync(CancellationToken cancellationToken);
}

public sealed class AuditPublicationAuthorizationException()
    : Exception("Audit envelope publication requires host authorization.");

internal static class AuditEnvelopeWriteAuthority
{
    internal static async ValueTask<IAuditEnvelopeWritePermit> AcquireAsync(
        IAuditEnvelopeWriteAuthorization? authorization, IReadOnlyList<AuditEnvelope> envelopes, CancellationToken ct)
    {
        if (authorization is null) throw new AuditPublicationAuthorizationException();
        var permit = await authorization.AcquireAsync(envelopes, ct);
        if (permit is null) throw new AuditPublicationAuthorizationException();
        try { await permit.CheckCurrentAsync(ct); return permit; }
        catch { await permit.DisposeAsync(); throw; }
    }
}
