using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace MillWorks.AuditCore.Abstractions.Canonicalization;

/// <summary>
/// Deterministic canonicalization of JSON data for tamper-evident hashing.
/// Decouples hash stability from ORM serialization order, preventing false
/// tamper alerts caused by dependency-induced serialization drift.
/// </summary>
public static class AuditCanonicalizer
{
    /// <summary>
    /// Algorithm version stored on integrity records.
    /// Documents which canonicalization and integrity algorithm produced a given hash.
    /// v1: Original format
    /// v2: JSON canonicalization with Unicode NFC normalization
    /// v3: Chain-position-aware HMAC/signature (includes eventHash, previousHash, sequenceNumber, timestamp)
    /// </summary>
    public const int CurrentVersion = 3;

    /// <summary>
    /// ISO 8601 output format with forced UTC and full fractional-second precision.
    /// All dates are converted to UTC before stringification.
    /// </summary>
    private const string _iso8601Format = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    /// <summary>
    /// Exact ISO 8601 formats accepted for date normalization.
    /// F = optional fractional seconds (up to 7 digits), K = Z or ±HH:mm offset.
    /// Using TryParseExact (not TryParse) to eliminate framework guesswork.
    /// Strings without a timezone offset are rejected (written verbatim) to prevent
    /// machine-dependent UTC conversion.
    /// </summary>
    private static readonly string[] _iso8601Formats =
    [
        "yyyy-MM-ddTHH:mm:ss.FFFFFFFK",
        "yyyy-MM-ddTHH:mm:ssK",
    ];

    /// <summary>
    /// Canonicalizes JSON data using deterministic normalization.
    /// <list type="bullet">
    /// <item>Object properties sorted alphabetically (ordinal, case-sensitive)</item>
    /// <item>Null properties explicitly included as <c>"key": null</c></item>
    /// <item>Array element order preserved (ordinal position is semantic)</item>
    /// <item>Empty arrays preserved as <c>[]</c></item>
    /// <item>DateTimeOffset/DateTime values converted to UTC ISO 8601</item>
    /// <item>Compact output (no whitespace)</item>
    /// </list>
    /// </summary>
    /// <param name="jsonData">Raw JSON string to canonicalize. Null/empty returns empty string.</param>
    /// <returns>Canonical JSON string.</returns>
    public static string Canonicalize(string? jsonData)
    {
        if (string.IsNullOrEmpty(jsonData))
            return string.Empty;

        using var doc = JsonDocument.Parse(jsonData);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               {
                   Indented = false,
                   SkipValidation = false
               }))
        {
            WriteCanonical(writer, doc.RootElement);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Recursively writes a JSON element in canonical form.
    /// </summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteCanonicalObject(writer, element);
                break;

            case JsonValueKind.Array:
                WriteCanonicalArray(writer, element);
                break;

            case JsonValueKind.String:
                WriteCanonicalString(writer, element);
                break;

            case JsonValueKind.Number:
                WriteCanonicalNumber(writer, element);
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;

            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;

            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;

            case JsonValueKind.Undefined:
            default:
                writer.WriteNullValue();
                break;
        }
    }

    /// <summary>
    /// Writes an object with properties sorted by key (ordinal, case-sensitive).
    /// All properties are included, including those with null values.
    /// </summary>
    private static void WriteCanonicalObject(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartObject();

        // Sort properties alphabetically by key (ordinal comparison for determinism)
        var properties = element.EnumerateObject().ToList();

        properties.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        foreach (var prop in properties)
        {
            writer.WritePropertyName(prop.Name);
            WriteCanonical(writer, prop.Value);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Writes an array preserving ordinal element order.
    /// Array contents are NOT sorted — <c>["B", "A"]</c> and <c>["A", "B"]</c>
    /// are semantically different data states.
    /// </summary>
    private static void WriteCanonicalArray(Utf8JsonWriter writer, JsonElement element)
    {
        writer.WriteStartArray();

        foreach (var item in element.EnumerateArray())
        {
            WriteCanonical(writer, item);
        }

        writer.WriteEndArray();
    }

    /// <summary>
    /// Writes a string value, normalizing ISO 8601 date/time values to UTC.
    /// Unicode strings are NFC-normalized for deterministic hashing across platforms.
    /// Non-date strings and strings without timezone offsets are written verbatim.
    /// </summary>
    private static void WriteCanonicalString(Utf8JsonWriter writer, JsonElement element)
    {
        var value = element.GetString();

        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        // Normalize Unicode to NFC (Composed) for deterministic hashing.
        // Prevents false tamper alerts from composed vs decomposed encoding differences
        // across Mac (NFD) / Windows (NFC) / external API sources.
        if (!value.IsNormalized(NormalizationForm.FormC))
            value = value.Normalize(NormalizationForm.FormC);

        // Structural pre-check: only attempt date parsing on strings that look like ISO 8601
        // (minimum: "yyyy-MM-ddTHH:mm:ss" = 19 chars, with '-' at [4] and [7], 'T' at [10])
        if (value.Length >= 19 && value[4] == '-' && value[7] == '-' && value[10] == 'T')
        {
            if (DateTimeOffset.TryParseExact(value, _iso8601Formats,
                    CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                // Reject no-offset strings: K matches empty, producing local time interpretation.
                // If the string has no 'Z', '+', or '-' after the time portion, it's ambiguous.
                var suffix = value.AsSpan(19);
                var hasOffset = value[^1] == 'Z' ||
                                suffix.Contains('+') ||
                                suffix.Contains('-');
                if (hasOffset)
                {
                    writer.WriteStringValue(dto.UtcDateTime.ToString(_iso8601Format, CultureInfo.InvariantCulture));
                    return;
                }
            }
        }

        // Non-date string or no-offset date — write verbatim (NFC-normalized)
        writer.WriteStringValue(value);
    }

    /// <summary>
    /// Writes a number value using an integer → decimal → double cascade
    /// to preserve exact representation for most common number types.
    /// </summary>
    private static void WriteCanonicalNumber(Utf8JsonWriter writer, JsonElement element)
    {
        if (element.TryGetInt64(out var longValue))
        {
            writer.WriteNumberValue(longValue);
        }
        else if (element.TryGetDecimal(out var decimalValue))
        {
            writer.WriteNumberValue(decimalValue);
        }
        else
        {
            writer.WriteNumberValue(element.GetDouble());
        }
    }

    /// <summary>
    /// Normalizes a <see cref="DateTimeOffset"/> to canonical UTC ISO 8601 string.
    /// </summary>
    public static string NormalizeDate(DateTimeOffset? value) => !value.HasValue
        ? string.Empty
        : value.Value.UtcDateTime.ToString(_iso8601Format, CultureInfo.InvariantCulture);
}
