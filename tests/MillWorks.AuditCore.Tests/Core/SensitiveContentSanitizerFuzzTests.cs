using System.Diagnostics;
using System.Text;
using FluentAssertions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// Phase 5: Fuzz-style tests for SensitiveContentSanitizer.
/// Throws randomized adversarial input at the sanitizer to detect crashes, hangs, or ReDoS.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase5")]
public sealed class SensitiveContentSanitizerFuzzTests
{
    private static readonly Random Rng = new(42);
    private const int FuzzIterations = 1000;
    private const int MaxTimeMs = 100; // per-call budget

    // ── No crash: sanitizer never throws for any input ──

    [Test]
    public void Fuzz_NoCrash_RandomStrings()
    {
        for (var i = 0; i < FuzzIterations; i++)
        {
            var input = GenerateRandomString(Rng, 1, 10_000);

            var act = () => SensitiveContentSanitizer.Sanitize(input);
            act.Should().NotThrow($"iteration {i}: sanitizer must not throw on random input");
        }
    }

    [Test]
    public void Fuzz_NoCrash_HighDensityDigitsAndDashes()
    {
        for (var i = 0; i < FuzzIterations; i++)
        {
            var sb = new StringBuilder(Rng.Next(100, 5000));
            for (var j = 0; j < sb.Capacity; j++)
            {
                sb.Append(Rng.Next(3) switch
                {
                    0 => (char)('0' + Rng.Next(10)),
                    1 => '-',
                    _ => (char)('a' + Rng.Next(26))
                });
            }

            var act = () => SensitiveContentSanitizer.Sanitize(sb.ToString());
            act.Should().NotThrow($"iteration {i}");
        }
    }

    [Test]
    public void Fuzz_NoCrash_HighDensityAtSignsAndDots()
    {
        for (var i = 0; i < FuzzIterations; i++)
        {
            var sb = new StringBuilder(Rng.Next(100, 5000));
            for (var j = 0; j < sb.Capacity; j++)
            {
                sb.Append(Rng.Next(4) switch
                {
                    0 => '@',
                    1 => '.',
                    2 => (char)('a' + Rng.Next(26)),
                    _ => (char)('0' + Rng.Next(10))
                });
            }

            var act = () => SensitiveContentSanitizer.Sanitize(sb.ToString());
            act.Should().NotThrow($"iteration {i}");
        }
    }

    // ── No hang: sanitizer completes within 100ms for inputs up to 100KB ──

    [Test]
    public void Fuzz_NoHang_LargeRandomStrings()
    {
        for (var i = 0; i < 50; i++)
        {
            var input = GenerateRandomString(Rng, 50_000, 100_000);

            var sw = Stopwatch.StartNew();
            SensitiveContentSanitizer.Sanitize(input, maxLength: 200_000);
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeLessThan(MaxTimeMs * 10, // relaxed for large inputs
                $"iteration {i}: sanitizer must not hang on large input ({input.Length} chars)");
        }
    }

    [Test]
    public void Fuzz_NoHang_ReDoSPayloads()
    {
        // Craft inputs designed to trigger catastrophic backtracking
        var reDoSPayloads = new[]
        {
            // Repeated group matching for connection string regex
            "server=" + new string('a', 10000) + ";",
            // Repeated group matching for bearer token regex
            "bearer " + new string('x', 10000),
            // Repeated email-like patterns
            string.Join(" ", Enumerable.Range(0, 1000).Select(j => $"a{j}@b{j}.c{j}")),
            // Repeated SSN-like patterns
            string.Join(" ", Enumerable.Range(0, 1000).Select(j => $"{j:D3}-{j % 100:D2}-{j:D4}")),
            // Nested quotes/parens for SQL pattern
            "key value is " + string.Concat(Enumerable.Repeat("('", 1000)) + "data" +
            string.Concat(Enumerable.Repeat("')", 1000)),
            // Alternating patterns
            string.Concat(Enumerable.Range(0, 5000).Select(j => j % 2 == 0 ? "password=" : "x")),
        };

        foreach (var payload in reDoSPayloads)
        {
            var sw = Stopwatch.StartNew();
            SensitiveContentSanitizer.Sanitize(payload, maxLength: payload.Length + 100);
            sw.Stop();

            sw.ElapsedMilliseconds.Should().BeLessThan(2000,
                $"sanitizer must not exhibit catastrophic backtracking (payload length: {payload.Length})");
        }
    }

    // ── Output validity: output is always a valid .NET string ──

    [Test]
    public void Fuzz_OutputValidity_AlwaysValidString()
    {
        for (var i = 0; i < FuzzIterations; i++)
        {
            var input = GenerateRandomString(Rng, 1, 5000);
            var result = SensitiveContentSanitizer.Sanitize(input);

            result.Should().NotBeNull($"iteration {i}");
            // Verify the string can be encoded to UTF-8 and back without loss
            var bytes = Encoding.UTF8.GetBytes(result);
            var decoded = Encoding.UTF8.GetString(bytes);
            decoded.Should().Be(result, $"iteration {i}: output must be valid UTF-8");
        }
    }

    // ── Surrogate pair handling ──

    [Test]
    public void Fuzz_NoCrash_SurrogatePairs()
    {
        for (var i = 0; i < 200; i++)
        {
            var sb = new StringBuilder();
            for (var j = 0; j < 100; j++)
            {
                if (Rng.Next(5) == 0)
                {
                    // Add a valid surrogate pair (emoji range)
                    var codePoint = 0x1F600 + Rng.Next(80);
                    sb.Append(char.ConvertFromUtf32(codePoint));
                }
                else
                {
                    sb.Append((char)('a' + Rng.Next(26)));
                }
            }

            var act = () => SensitiveContentSanitizer.Sanitize(sb.ToString());
            act.Should().NotThrow($"iteration {i}: surrogate pairs must not crash sanitizer");
        }
    }

    // ── BOM markers ──

    [Test]
    public void Fuzz_NoCrash_BOMMarkers()
    {
        var inputs = new[]
        {
            "\uFEFF" + "normal text with BOM",
            "text with embedded \uFEFF BOM",
            "\uFEFF\uFEFF\uFEFF" + "triple BOM",
            "\uFEFF" + "password=secret",
        };

        foreach (var input in inputs)
        {
            var act = () => SensitiveContentSanitizer.Sanitize(input);
            act.Should().NotThrow("BOM markers must not crash sanitizer");
        }
    }

    // ── Mixed sensitive patterns under fuzz ──

    [Test]
    public void Fuzz_MixedPatterns_NoHang()
    {
        for (var i = 0; i < 200; i++)
        {
            var sb = new StringBuilder(5000);
            for (var j = 0; j < 100; j++)
            {
                switch (Rng.Next(6))
                {
                    case 0: sb.Append($"server=host{j};password=pw{j};"); break;
                    case 1: sb.Append($"user{j}@domain{j}.com "); break;
                    case 2: sb.Append($"{Rng.Next(100, 999):D3}-{Rng.Next(10, 99):D2}-{Rng.Next(1000, 9999):D4} "); break;
                    case 3: sb.Append($"bearer token{j} "); break;
                    case 4: sb.Append($"key value is ('{j}') "); break;
                    default: sb.Append($"safe text chunk {j} "); break;
                }
            }

            var sw = Stopwatch.StartNew();
            var result = SensitiveContentSanitizer.Sanitize(sb.ToString(), maxLength: 100_000);
            sw.Stop();

            result.Should().NotBeNull();
            sw.ElapsedMilliseconds.Should().BeLessThan(MaxTimeMs * 5,
                $"iteration {i}: mixed patterns must not cause slowdown");
        }
    }

    // ── Concurrent fuzz ──

    [Test]
    public void Fuzz_ConcurrentSanitization_NoRace()
    {
        var inputs = Enumerable.Range(0, 200)
            .Select(_ => GenerateRandomString(Rng, 100, 5000))
            .ToArray();

        var results = new string[inputs.Length];

        Parallel.For(0, inputs.Length, i =>
        {
            results[i] = SensitiveContentSanitizer.Sanitize(inputs[i]);
        });

        results.Should().AllSatisfy(r => r.Should().NotBeNull());
    }

    #region Generators

    private static string GenerateRandomString(Random rng, int minLen, int maxLen)
    {
        var len = rng.Next(minLen, maxLen + 1);
        var sb = new StringBuilder(len);
        for (var i = 0; i < len; i++)
        {
            sb.Append((char)(rng.Next(32, 127))); // printable ASCII
        }
        return sb.ToString();
    }

    #endregion
}
