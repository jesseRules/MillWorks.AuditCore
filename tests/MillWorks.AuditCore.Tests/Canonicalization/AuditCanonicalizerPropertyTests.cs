using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MillWorks.AuditCore.Abstractions.Canonicalization;

namespace MillWorks.AuditCore.Tests.Canonicalization;

/// <summary>
/// Phase 5: Property-based tests for AuditCanonicalizer.
/// Uses randomized inputs over 1000+ iterations to verify canonicalization properties.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class AuditCanonicalizerPropertyTests
{
    private static readonly Random Rng = new(42); // deterministic seed for reproducibility

    // ── Determinism: same input always yields same output ──

    [Test]
    public void Property_Determinism_SameInputSameOutput()
    {
        for (var i = 0; i < 1000; i++)
        {
            var json = GenerateRandomJson(Rng);
            var result1 = AuditCanonicalizer.Canonicalize(json);
            var result2 = AuditCanonicalizer.Canonicalize(json);

            result1.Should().Be(result2, $"iteration {i}: canonicalization must be deterministic");
        }
    }

    // ── Stability: reordering object keys doesn't change canonical form ──

    [Test]
    public void Property_Stability_KeyReorderingDoesNotChangeOutput()
    {
        for (var i = 0; i < 500; i++)
        {
            var keys = Enumerable.Range(0, Rng.Next(2, 8))
                .Select(_ => RandomAlphaString(Rng, 3, 10))
                .Distinct().ToList();
            var values = keys.Select(_ => RandomJsonValue(Rng)).ToList();

            // Build JSON with original key order
            var json1 = BuildJsonObject(keys, values);
            // Build JSON with shuffled key order
            var shuffled = keys.Zip(values).OrderBy(_ => Rng.Next()).ToList();
            var json2 = BuildJsonObject(
                shuffled.Select(x => x.First).ToList(),
                shuffled.Select(x => x.Second).ToList());

            var result1 = AuditCanonicalizer.Canonicalize(json1);
            var result2 = AuditCanonicalizer.Canonicalize(json2);

            result1.Should().Be(result2, $"iteration {i}: key reordering must not change canonical form");
        }
    }

    // ── Sensitivity: changing any field value changes canonical output ──

    [Test]
    public void Property_Sensitivity_ChangingOneFieldChangesOutput()
    {
        for (var i = 0; i < 500; i++)
        {
            var json1 = $"{{\"a\":\"{RandomAlphaString(Rng, 5, 20)}\",\"b\":{Rng.Next(1, 10000)}}}";
            // Change the "a" field value
            var json2 = $"{{\"a\":\"{RandomAlphaString(Rng, 5, 20)}_modified\",\"b\":{Rng.Next(1, 10000)}}}";

            var result1 = AuditCanonicalizer.Canonicalize(json1);
            var result2 = AuditCanonicalizer.Canonicalize(json2);

            result1.Should().NotBe(result2, $"iteration {i}: different field values should produce different canonical forms");
        }
    }

    // ── Completeness: adding a value to any field changes output ──

    [Test]
    public void Property_Completeness_AddingFieldChangesOutput()
    {
        for (var i = 0; i < 500; i++)
        {
            var val = RandomAlphaString(Rng, 3, 10);
            var baseJson = $"{{\"field1\":\"{val}\"}}";
            var extendedJson = $"{{\"field1\":\"{val}\",\"field2\":\"extra\"}}";

            var result1 = AuditCanonicalizer.Canonicalize(baseJson);
            var result2 = AuditCanonicalizer.Canonicalize(extendedJson);

            result1.Should().NotBe(result2, $"iteration {i}: adding a field must change canonical form");
        }
    }

    // ── Null safety: events with null fields produce valid output ──

    [Test]
    public void Property_NullSafety_NullFieldCombinations()
    {
        var fields = new[] { "a", "b", "c", "d", "e" };
        for (var i = 0; i < 500; i++)
        {
            var entries = fields.Select(f =>
            {
                var isNull = Rng.Next(2) == 0;
                return $"\"{f}\":{(isNull ? "null" : $"\"{RandomAlphaString(Rng, 1, 5)}\"")}";
            });
            var json = "{" + string.Join(",", entries) + "}";

            var act = () => AuditCanonicalizer.Canonicalize(json);
            act.Should().NotThrow($"iteration {i}: null fields must not cause exceptions");

            var result = AuditCanonicalizer.Canonicalize(json);
            result.Should().NotBeNull();
            result.Should().NotBeEmpty();
        }
    }

    // ── Unicode normalization: NFC vs NFD produce same output ──

    [Test]
    public void Property_UnicodeNormalization_NFCEqualsNFD()
    {
        // Characters with composed (NFC) and decomposed (NFD) forms
        var testChars = new[] { "\u00E9", "\u00F1", "\u00FC", "\u00E0", "\u00F6" }; // é, ñ, ü, à, ö
        for (var i = 0; i < 200; i++)
        {
            var ch = testChars[Rng.Next(testChars.Length)];
            var nfcValue = ch.Normalize(System.Text.NormalizationForm.FormC);
            var nfdValue = ch.Normalize(System.Text.NormalizationForm.FormD);

            var jsonNfc = $"{{\"name\":\"{nfcValue}test{i}\"}}";
            var jsonNfd = $"{{\"name\":\"{nfdValue}test{i}\"}}";

            var resultNfc = AuditCanonicalizer.Canonicalize(jsonNfc);
            var resultNfd = AuditCanonicalizer.Canonicalize(jsonNfd);

            resultNfc.Should().Be(resultNfd,
                $"iteration {i}: NFC and NFD forms of '{ch}' must produce same canonical output");
        }
    }

    // ── Injection resistance: field values containing separator don't cause ambiguity ──

    [Test]
    public void Property_InjectionResistance_PipeSeparatorInValues()
    {
        for (var i = 0; i < 500; i++)
        {
            // Include pipe character (used as separator in hash computation)
            var injectedValue = $"value|with|pipes|{Rng.Next(1000)}";
            var normalValue = $"value_without_pipes_{Rng.Next(1000)}";

            var json1 = $"{{\"field\":\"{injectedValue}\"}}";
            var json2 = $"{{\"field\":\"{normalValue}\"}}";

            var result1 = AuditCanonicalizer.Canonicalize(json1);
            var result2 = AuditCanonicalizer.Canonicalize(json2);

            // Different values must produce different canonical forms
            result1.Should().NotBe(result2, $"iteration {i}: pipe separators in values must not cause ambiguity");

            // And the pipe is preserved in output
            result1.Should().Contain("|");
        }
    }

    // ── Edge case generators ──

    [Test]
    public void Property_AllFieldsNull()
    {
        var json = "{\"a\":null,\"b\":null,\"c\":null,\"d\":null,\"e\":null}";
        var result = AuditCanonicalizer.Canonicalize(json);
        result.Should().NotBeNull();
        result.Should().Contain("null");
    }

    [Test]
    public void Property_ControlCharactersInValues()
    {
        // Control characters that are valid in JSON strings when escaped
        var testInputs = new[]
        {
            "{\"field\":\"hello\\nworld\"}",
            "{\"field\":\"tab\\there\"}",
            "{\"field\":\"cr\\rreturn\"}",
            "{\"field\":\"back\\bspace\"}",
        };

        foreach (var input in testInputs)
        {
            var act = () => AuditCanonicalizer.Canonicalize(input);
            act.Should().NotThrow();
        }
    }

    [Test]
    public void Property_NestedObjectsWithEscapedCharacters()
    {
        var json = "{\"data\":{\"quote\":\"she said \\\"hello\\\"\",\"backslash\":\"path\\\\file\"}}";
        var result = AuditCanonicalizer.Canonicalize(json);

        result.Should().NotBeNull();
        // Utf8JsonWriter uses \u0022 for quotes — verify the content is preserved
        result.Should().Contain("hello");
        result.Should().Contain("path");
    }

    [Test]
    public void Property_LargeMaxLengthFields()
    {
        var longValue = new string('x', 50_000);
        var json = $"{{\"field\":\"{longValue}\"}}";

        var act = () => AuditCanonicalizer.Canonicalize(json);
        act.Should().NotThrow();

        var result = AuditCanonicalizer.Canonicalize(json);
        result.Should().Contain(longValue);
    }

    // ── Date normalization properties ──

    [Test]
    public void Property_DateNormalization_SameInstantSameOutput()
    {
        for (var i = 0; i < 200; i++)
        {
            var year = Rng.Next(2000, 2030);
            var month = Rng.Next(1, 13);
            var day = Rng.Next(1, 28);
            var hour = Rng.Next(0, 24);
            var minute = Rng.Next(0, 60);
            var offsetHours = Rng.Next(-12, 13);

            var dto = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromHours(offsetHours));
            var utcDto = dto.ToOffset(TimeSpan.Zero);

            var jsonWithOffset = $"{{\"ts\":\"{dto:yyyy-MM-ddTHH:mm:ss.fffffffzzz}\"}}";
            var jsonUtc = $"{{\"ts\":\"{utcDto:yyyy-MM-ddTHH:mm:ss.fffffffZ}\"}}";

            var result1 = AuditCanonicalizer.Canonicalize(jsonWithOffset);
            var result2 = AuditCanonicalizer.Canonicalize(jsonUtc);

            result1.Should().Be(result2, $"iteration {i}: same instant in different timezones must canonicalize identically");
        }
    }

    // ── Hash stability: canonical form produces stable SHA-256 ──

    [Test]
    public void Property_HashStability_CanonicalFormProducesStableHash()
    {
        for (var i = 0; i < 500; i++)
        {
            var json = GenerateRandomJson(Rng);
            var canonical = AuditCanonicalizer.Canonicalize(json);

            var hash1 = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            var hash2 = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

            hash1.Should().BeEquivalentTo(hash2);
        }
    }

    #region Generators

    private static string GenerateRandomJson(Random rng, int depth = 0)
    {
        var fieldCount = rng.Next(1, 6);
        var entries = new List<string>();
        for (var i = 0; i < fieldCount; i++)
        {
            var key = RandomAlphaString(rng, 1, 8);
            string value;
            if (depth < 2 && rng.Next(5) == 0)
                value = GenerateRandomJson(rng, depth + 1);
            else
                value = RandomJsonValue(rng);
            entries.Add($"\"{key}\":{value}");
        }
        return "{" + string.Join(",", entries) + "}";
    }

    private static string RandomJsonValue(Random rng)
    {
        return (rng.Next(5)) switch
        {
            0 => "null",
            1 => rng.Next(-1000, 1000).ToString(),
            2 => rng.Next(2) == 0 ? "true" : "false",
            3 => $"\"{RandomAlphaString(rng, 0, 20)}\"",
            _ => $"{rng.NextDouble():F4}",
        };
    }

    private static string RandomAlphaString(Random rng, int minLen, int maxLen)
    {
        var len = rng.Next(minLen, maxLen + 1);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
            sb.Append((char)('a' + rng.Next(26)));
        return sb.ToString();
    }

    private static string BuildJsonObject(List<string> keys, List<string> values)
    {
        var entries = keys.Zip(values, (k, v) => $"\"{k}\":{v}");
        return "{" + string.Join(",", entries) + "}";
    }

    #endregion
}
