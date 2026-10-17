using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Enums;
using MillWorks.AuditCore.Abstractions.Models;

namespace MillWorks.AuditCore.Services.Sinks.Identity;

/// <summary>
/// Versioned comparison of the semantic input to an audit sink. This is comparison evidence,
/// not an integrity-chain proof or authorization decision. Compute before sink redaction;
/// never return the canonical payload or fingerprint through replay/conflict responses.
/// </summary>
public static class AuditEnvelopeContent
{
    /// <summary>Immutable comparison format. Changing field selection or normalization requires a new version.</summary>
    public const int Version = 1;

    /// <summary>
    /// Computes a SHA-256 comparison digest. EnvelopeId, ExplicitEventId and transport state
    /// are excluded; the logical identity is stored separately. Explicit occurrence time is
    /// semantic. Entity capture time is excluded because the interceptor reconstructs it on retry.
    /// Subject links and property diffs are sets; payload arrays retain their order.
    /// </summary>
    public static string Fingerprint(AuditEnvelope envelope)
    {
        Validate(envelope);
        var semantic = JsonSerializer.SerializeToElement(new
        {
            Format = "AuditCore.EnvelopeContent.v1",
            envelope.Kind,
            envelope.EntityName,
            envelope.EntityId,
            envelope.EntityIdString,
            envelope.Action,
            envelope.UserId,
            envelope.CorrelationId,
            envelope.IpAddress,
            envelope.UserAgent,
            OccurredAt = envelope.Kind == AuditEnvelopeKind.ExplicitEvent
                ? envelope.OccurredAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) : null,
            envelope.EventType,
            envelope.Description,
            AdditionalData = Payload(envelope.AdditionalData),
            envelope.GovernanceIdentity,
            SubjectReferences = (envelope.SubjectReferences ?? [])
                .DistinctBy(x => (x.SubjectId, x.Relationship))
                .OrderBy(x => x.SubjectId).ThenBy(x => x.Relationship, StringComparer.Ordinal),
            PropertyChanges = (envelope.PropertyChanges ?? [])
                .OrderBy(x => x.PropertyName, StringComparer.Ordinal)
        });
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) WriteCanonical(writer, semantic);
        return Convert.ToHexString(SHA256.HashData(buffer.WrittenSpan));
    }

    /// <summary>Validates all identities before a batch can produce persistence side effects.</summary>
    internal static void ValidateBatch(IReadOnlyList<AuditEnvelope> envelopes)
    {
        ArgumentNullException.ThrowIfNull(envelopes);
        var ids = new HashSet<Guid>();
        for (var index = 0; index < envelopes.Count; index++)
        {
            var envelope = envelopes[index];
            if (envelope is null)
                throw new ArgumentException($"Batch entry at index {index} must not be null.", nameof(envelopes));
            Validate(envelope);
            if (!ids.Add(envelope.EnvelopeId))
                throw new ArgumentException("Batch contains repeated EnvelopeIds.", nameof(envelopes));
        }
    }

    /// <summary>Checks logical and correlation identity, kind, required fields and unambiguous property/subject inputs.</summary>
    internal static void Validate(AuditEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.EnvelopeId == Guid.Empty || !Enum.IsDefined(envelope.Kind) || !Enum.IsDefined(envelope.Action) ||
            string.IsNullOrWhiteSpace(envelope.EntityName))
            throw new ArgumentException("Invalid audit envelope contract.", nameof(envelope));
        if (envelope.Kind == AuditEnvelopeKind.ExplicitEvent &&
            (envelope.ExplicitEventId is null || envelope.ExplicitEventId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.EventType)))
            throw new ArgumentException("Explicit events require a non-empty ExplicitEventId and EventType.", nameof(envelope));
        // Validate original strings before JsonSerializer can replace invalid UTF-16 with U+FFFD.
        foreach (var text in new[] { envelope.EntityName, envelope.EntityIdString, envelope.UserId,
                     envelope.CorrelationId, envelope.IpAddress, envelope.UserAgent, envelope.EventType,
                     envelope.Description, envelope.AdditionalData, envelope.GovernanceIdentity?.ResourceType })
            if (text is not null) ValidateString(text);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in envelope.PropertyChanges ?? [])
        {
            if (property is null || string.IsNullOrWhiteSpace(property.PropertyName) || !names.Add(property.PropertyName))
                throw new ArgumentException("Property changes must have distinct non-empty names.", nameof(envelope));
            ValidateString(property.PropertyName);
            if (property.OldValue is not null) ValidateString(property.OldValue);
            if (property.NewValue is not null) ValidateString(property.NewValue);
        }
        foreach (var subject in envelope.SubjectReferences ?? [])
        {
            if (subject is null) throw new ArgumentException("Subject references must not contain nulls.", nameof(envelope));
            ValidateString(subject.Relationship);
        }
    }

    private static object? Payload(string? text)
    {
        if (text is null) return null;
        try
        {
            using var document = JsonDocument.Parse(text);
            return new { Format = "json", Value = document.RootElement.Clone() };
        }
        catch (JsonException) { return new { Format = "text", Value = text }; }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (!names.Add(property.Name)) throw new ArgumentException("JSON payload contains repeated property names.");
                    ValidateString(property.Name);
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                var text = value.GetString()!;
                ValidateString(text);
                writer.WriteStringValue(text);
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(NormalizeNumber(value.GetRawText()));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static void ValidateString(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (!char.IsSurrogate(text[i])) continue;
            if (!char.IsHighSurrogate(text[i]) || i + 1 == text.Length || !char.IsLowSurrogate(text[++i]))
                throw new ArgumentException("JSON contains an invalid Unicode scalar.");
        }
    }

    // Exact decimal coefficient/exponent normalization; never round identity content through double.
    private static string NormalizeNumber(string text)
    {
        var negative = text[0] == '-';
        var unsigned = negative ? text[1..] : text;
        var exponentAt = unsigned.IndexOfAny(['e', 'E']);
        var coefficient = exponentAt < 0 ? unsigned : unsigned[..exponentAt];
        var exponent = exponentAt < 0 ? BigInteger.Zero
            : BigInteger.Parse(unsigned[(exponentAt + 1)..], CultureInfo.InvariantCulture);
        var dot = coefficient.IndexOf('.');
        if (dot >= 0)
        {
            exponent -= coefficient.Length - dot - 1;
            coefficient = coefficient.Remove(dot, 1);
        }
        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length == 0) return "0";
        var trimmed = coefficient.TrimEnd('0');
        exponent += coefficient.Length - trimmed.Length;
        return (negative ? "-" : "") + trimmed + (exponent.IsZero ? "" : "e" + exponent.ToString(CultureInfo.InvariantCulture));
    }
}
