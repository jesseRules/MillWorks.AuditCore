using System.Diagnostics;
using FluentAssertions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// Phase 4: Adversarial input tests for SensitiveContentSanitizer.
/// Validates pattern detection, false-positive handling, performance, and thread safety.
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("Phase4")]
public sealed class SensitiveContentSanitizerAdversarialTests
{
    // ── SSN patterns ──

    [Test]
    public void Sanitize_SSN_WithDashes_IsMasked()
    {
        var input = "Patient SSN: 123-45-6789 on file";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("123-45-6789");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_SSN_EmbeddedInText_IsMasked()
    {
        var input = "Error: duplicate record for SSN=123-45-6789 in table";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("123-45-6789");
    }

    // ── Email addresses ──

    [Test]
    public void Sanitize_Email_Standard_IsMasked()
    {
        var input = "User patient.john@medical-center.org logged in";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("patient.john@medical-center.org");
    }

    [Test]
    public void Sanitize_Email_InSqlError_IsMasked()
    {
        var input = "UNIQUE constraint failed: users.email = 'admin+test@hospital.com'";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("admin+test@hospital.com");
    }

    // ── Bearer tokens and API keys ──

    [Test]
    public void Sanitize_BearerToken_WithAuthorizationPrefix_IsMasked()
    {
        // The regex matches "Authorization: <token>" as a whole — the Bearer keyword
        // within the auth value is consumed by the \S+ after the separator.
        var input = "Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_StandaloneBearerToken_IsMasked()
    {
        // "Bearer <token>" without preceding "Authorization:" header
        var input = "Failed auth. Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_ApiKey_IsMasked()
    {
        var input = "api_key=sk_live_1234567890abcdef";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("sk_live_1234567890abcdef");
    }

    [Test]
    public void Sanitize_TokenHeader_IsMasked()
    {
        var input = "token: abc123def456";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("abc123def456");
    }

    // ── Connection strings ──

    [Test]
    public void Sanitize_ConnectionString_FullSqlServer_RemovesCredentials()
    {
        var input = "Server=prod.db.com;User Id=sa;Password=P@ssw0rd!;Database=AuditDb;";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("P@ssw0rd!");
        result.Should().NotContain("sa");
    }

    [Test]
    public void Sanitize_ConnectionString_PostgreSQL_RemovesCredentials()
    {
        var input = "Host=pg.internal;uid=dbuser;pwd=secret123;Database=audit";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("secret123");
        result.Should().NotContain("dbuser");
    }

    // ── SQL key value violations ──

    [Test]
    public void Sanitize_SqlDuplicateKeyValue_IsMasked()
    {
        var input = "The duplicate key value is ('john.doe@example.com').";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_SqlDuplicateKeyValue_WithKeyValueIs_IsMasked()
    {
        // "key value is" pattern is matched by the regex
        var input = "The duplicate key value is ('sensitive-patient-id').";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("[SANITIZED]");
    }

    // ── Mixed sensitive data ──

    [Test]
    public void Sanitize_MixedSensitiveData_KnownPatternsCaught()
    {
        var input = "Error: User admin@corp.com (SSN: 123-45-6789) failed auth. " +
                    "Token: mytoken123. Server=db.local;Password=secret;";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("admin@corp.com");
        result.Should().NotContain("123-45-6789");
        result.Should().NotContain("secret");
        // Token pattern should match "Token: <value>"
        result.Should().NotContain("mytoken123");
    }

    // ── Safe content preservation ──

    [Test]
    public void Sanitize_SafeContent_IsPreserved()
    {
        var input = "Connection timeout after 30 seconds. Retry count: 3";
        SensitiveContentSanitizer.Sanitize(input).Should().Be(input);
    }

    [Test]
    public void Sanitize_TechnicalErrorMessage_PreservesStructure()
    {
        var input = "NullReferenceException at MyService.ProcessAsync() line 42";
        SensitiveContentSanitizer.Sanitize(input).Should().Be(input);
    }

    [Test]
    public void Sanitize_NumbersThatAreNotSSN_Preserved()
    {
        // Dates, zip codes, port numbers should not be sanitized
        var input = "Request at 2026-04-02 to port 8080 from zip 12345";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("2026-04-02");
        result.Should().Contain("8080");
        result.Should().Contain("12345");
    }

    // ── Null and empty ──

    [Test]
    public void Sanitize_Null_ReturnsEmpty()
    {
        SensitiveContentSanitizer.Sanitize(null).Should().BeEmpty();
    }

    [Test]
    public void Sanitize_Empty_ReturnsEmpty()
    {
        SensitiveContentSanitizer.Sanitize("").Should().BeEmpty();
    }

    // ── Truncation ──

    [Test]
    public void Sanitize_ExceedsMaxLength_Truncates()
    {
        var input = new string('x', 1000);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 100);

        // Total output must not exceed maxLength
        result.Length.Should().BeLessThanOrEqualTo(100);
        result.Should().EndWith("...[truncated]");
    }

    [Test]
    public void Sanitize_ExactlyMaxLength_NoTruncation()
    {
        var input = new string('a', 512);
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Be(input);
        result.Should().NotContain("[truncated]");
    }

    [Test]
    public void Sanitize_UnderMaxLength_NoTruncation()
    {
        var input = "Short message";
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 512);

        result.Should().Be(input);
    }

    // ── Performance / ReDoS safety ──

    [Test]
    public void Sanitize_LargeInput_CompletesInReasonableTime()
    {
        // 100KB of mixed content that could trigger catastrophic backtracking
        var largeInput = string.Join(" ", Enumerable.Range(0, 5000)
            .Select(i => $"field{i}=value{i};"));

        var sw = Stopwatch.StartNew();
        var result = SensitiveContentSanitizer.Sanitize(largeInput, maxLength: 200_000);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(5000,
            "sanitizer should not exhibit catastrophic backtracking");
        result.Should().NotBeNullOrEmpty();
    }

    [Test]
    public void Sanitize_ReDoSPayload_CompletesQuickly()
    {
        // Craft input designed to trigger ReDoS on naive regex patterns
        var payload = "api_key=" + new string('a', 10000);

        var sw = Stopwatch.StartNew();
        SensitiveContentSanitizer.Sanitize(payload);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(2000);
    }

    // ── Thread safety ──

    [Test]
    public void Sanitize_ConcurrentCalls_AllSucceed()
    {
        var inputs = Enumerable.Range(0, 50)
            .Select(i => $"User user{i}@test.com logged in with token: tok_{i}")
            .ToList();

        var results = new string[inputs.Count];

        Parallel.ForEach(inputs, (input, _, index) =>
        {
            results[(int)index] = SensitiveContentSanitizer.Sanitize(input);
        });

        results.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNullOrEmpty();
            r.Should().Contain("[SANITIZED]");
        });
    }

    // ── Statelessness ──

    [Test]
    public void Sanitize_IsStateless_SameInputSameOutput()
    {
        var input = "Password=secret123 and email@test.com";
        var result1 = SensitiveContentSanitizer.Sanitize(input);
        var result2 = SensitiveContentSanitizer.Sanitize(input);

        result1.Should().Be(result2);
    }
}
