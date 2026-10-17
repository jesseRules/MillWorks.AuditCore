using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks.Recovery;

internal static class QueuedEnvelopeValidator
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal static AuditEnvelope Read(string payload, int version)
    {
        if (version != TransactionalOutboxSink.CurrentEnvelopeVersion) throw new InvalidDataException("unsupported_version");
        try
        {
            using var document = JsonDocument.Parse(payload);
            var json = document.RootElement;
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("envelopeId", out var identity) || !identity.TryGetGuid(out var id) || id == Guid.Empty ||
                !json.TryGetProperty("kind", out var kind) || !kind.TryGetInt32(out var kindValue) || !Enum.IsDefined((AuditEnvelopeKind)kindValue) ||
                !json.TryGetProperty("occurredAt", out var timestamp) || !timestamp.TryGetDateTimeOffset(out _) ||
                !json.TryGetProperty("action", out var action) || !action.TryGetInt32(out var actionValue) || !Enum.IsDefined((AuditAction)actionValue))
                throw new InvalidDataException("invalid_contract");
            var envelope = JsonSerializer.Deserialize<AuditEnvelope>(payload, Json) ?? throw new InvalidDataException("invalid_contract");
            if (string.IsNullOrWhiteSpace(envelope.EntityName) ||
                (envelope.Kind == AuditEnvelopeKind.ExplicitEvent && string.IsNullOrWhiteSpace(envelope.EventType)) ||
                (envelope.PropertyChanges?.Any(x => x is null || string.IsNullOrWhiteSpace(x.PropertyName)) ?? false))
                throw new InvalidDataException("invalid_contract");
            Identity.AuditEnvelopeContent.Validate(envelope);
            _ = Identity.AuditEnvelopeContent.Fingerprint(envelope);
            return envelope;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        {
            // Do not retain exception messages containing serialized input or SQL details.
            throw new InvalidDataException("invalid_contract");
        }
    }

    internal static Guid? TenantId(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("governanceIdentity", out var governance) &&
                   governance.ValueKind == JsonValueKind.Object && governance.TryGetProperty("tenant_id", out var tenant) &&
                   tenant.TryGetGuid(out var id) && id != Guid.Empty ? id : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
    }
}
