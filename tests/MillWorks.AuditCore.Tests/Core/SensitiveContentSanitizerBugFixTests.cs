using FluentAssertions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

/// <summary>
/// Tests for the three BugsFound.md open issues:
/// 1. [SECURITY] SQL INSERT values pattern gaps
/// 2. [ENHANCEMENT] Missing PII patterns (hyphen-free SSNs, credit cards, phone numbers)
/// 3. [ENHANCEMENT] Truncation exceeds maxLength contract
/// </summary>
[TestFixture]
[Category("Unit")]
[Category("BugFix")]
public sealed class SensitiveContentSanitizerBugFixTests
{
    // ════════════════════════════════════════════════════════════════════
    // Bug 1: SQL INSERT values pattern
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_SqlValuesNoSpace_IsMasked()
    {
        // The original regex required a space before the paren: "values ("
        var input = "Cannot insert duplicate key row. values('patient-data')";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("patient-data");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_SqlValuesWithSpace_StillMasked()
    {
        var input = "Cannot insert duplicate key. values ('sensitive-id')";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("sensitive-id");
    }

    [Test]
    public void Sanitize_SqlKeyValueIs_WithVariableWhitespace_IsMasked()
    {
        var input = "The duplicate key  value  is ('patient@hospital.org').";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_InsertIntoWithValues_IsMasked()
    {
        var input = "INSERT INTO patients (name, ssn) VALUES ('John Doe', '123-45-6789')";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("John Doe");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_InsertIntoNoSpace_IsMasked()
    {
        var input = "INSERT INTO users(email) VALUES('admin@example.com')";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("admin@example.com");
    }

    [Test]
    public void Sanitize_GuidInConstraintViolation_IsMasked()
    {
        var input = "Violation of UNIQUE KEY constraint 'IX_PatientId'. " +
                    "Duplicate key value is (a1b2c3d4-e5f6-7890-abcd-ef1234567890).";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    }

    [Test]
    public void Sanitize_GuidInDuplicateKeyError_IsMasked()
    {
        var input = "duplicate key value violates unique constraint: " +
                    "f47ac10b-58cc-4372-a567-0e02b2c3d479";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("f47ac10b-58cc-4372-a567-0e02b2c3d479");
    }

    [Test]
    public void Sanitize_GuidNotInConstraintContext_IsPreserved()
    {
        // GUIDs outside of constraint/violation context should not be masked
        var input = "Request f47ac10b-58cc-4372-a567-0e02b2c3d479 completed successfully";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("f47ac10b-58cc-4372-a567-0e02b2c3d479");
    }

    // ════════════════════════════════════════════════════════════════════
    // Bug 2a: Hyphen-free SSN patterns
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_SSN_WithoutHyphens_IsMasked()
    {
        var input = "Patient SSN: 123456789 on file";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("123456789");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InErrorMessage_IsMasked()
    {
        var input = "Duplicate record found for identifier 234567890";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("234567890");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InvalidArea000_NotMasked()
    {
        // SSA rule: area 000 is invalid
        var input = "Code 000123456 processed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("000123456");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InvalidArea666_NotMasked()
    {
        // SSA rule: area 666 is invalid
        var input = "Code 666123456 processed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("666123456");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InvalidArea9xx_NotMasked()
    {
        // SSA rule: area 900-999 is invalid
        var input = "Code 900123456 processed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("900123456");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InvalidGroup00_NotMasked()
    {
        // SSA rule: group 00 is invalid — 123004567 should not match
        var input = "Code 123004567 processed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("123004567");
    }

    [Test]
    public void Sanitize_SSN_WithoutHyphens_InvalidSerial0000_NotMasked()
    {
        // SSA rule: serial 0000 is invalid — 123450000 should not match
        var input = "Code 123450000 processed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("123450000");
    }

    [Test]
    public void Sanitize_TenDigitNumber_NotMaskedAsSSN()
    {
        // 10+ consecutive digits should not be partially matched
        var input = "Transaction 1234567890 completed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("1234567890");
    }

    [Test]
    public void Sanitize_FiveDigitZipCode_NotMaskedAsSSN()
    {
        var input = "Zip code 12345 is valid";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("12345");
    }

    [Test]
    public void Sanitize_PortNumber_NotMaskedAsSSN()
    {
        var input = "Connected on port 8080";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("8080");
    }

    // ════════════════════════════════════════════════════════════════════
    // Bug 2b: Credit card patterns
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_Visa_NoDashes_IsMasked()
    {
        var input = "Card: 4111111111111111";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("4111111111111111");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_Visa_WithDashes_IsMasked()
    {
        var input = "Card: 4111-1111-1111-1111";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("4111-1111-1111-1111");
    }

    [Test]
    public void Sanitize_Visa_WithSpaces_IsMasked()
    {
        var input = "Card: 4111 1111 1111 1111";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("4111 1111 1111 1111");
    }

    [Test]
    public void Sanitize_Mastercard_IsMasked()
    {
        var input = "Payment with 5500000000000004";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("5500000000000004");
    }

    [Test]
    public void Sanitize_Amex_IsMasked()
    {
        var input = "Amex card 378282246310005";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("378282246310005");
    }

    [Test]
    public void Sanitize_Discover_IsMasked()
    {
        var input = "Discover 6011111111111117";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("6011111111111117");
    }

    [Test]
    public void Sanitize_CreditCard_InSqlError_IsMasked()
    {
        var input = "Duplicate entry '4111111111111111' for key 'payment_card_idx'";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("4111111111111111");
    }

    [Test]
    public void Sanitize_ShortNumber_NotMaskedAsCreditCard()
    {
        // 8-digit numbers should not trigger credit card detection
        var input = "Order 41112222 placed";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Contain("41112222");
    }

    // ════════════════════════════════════════════════════════════════════
    // Bug 2c: Phone number patterns
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_USPhone_DashFormat_IsMasked()
    {
        var input = "Contact: 212-555-1234";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("212-555-1234");
        result.Should().Contain("[SANITIZED]");
    }

    [Test]
    public void Sanitize_USPhone_ParenFormat_IsMasked()
    {
        var input = "Call (212) 555-1234 for info";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("(212) 555-1234");
    }

    [Test]
    public void Sanitize_USPhone_DotFormat_IsMasked()
    {
        var input = "Phone: 212.555.1234";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("212.555.1234");
    }

    [Test]
    public void Sanitize_USPhone_WithCountryCode_IsMasked()
    {
        var input = "Fax: +1-212-555-1234";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("+1-212-555-1234");
    }

    [Test]
    public void Sanitize_USPhone_WithCountryCodeDots_IsMasked()
    {
        var input = "Phone: 1.212.555.1234";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("1.212.555.1234");
    }

    [Test]
    public void Sanitize_PhoneInErrorMessage_IsMasked()
    {
        var input = "Patient contact 555-867-5309 already on file";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("555-867-5309");
    }

    // ════════════════════════════════════════════════════════════════════
    // Bug 3: Truncation must honor maxLength contract
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_Truncation_ExactlyMaxLength()
    {
        var input = new string('a', 1000);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 200);

        result.Length.Should().Be(200, "output must be exactly maxLength");
        result.Should().EndWith("...[truncated]");
    }

    [Test]
    public void Sanitize_Truncation_NeverExceedsMaxLength()
    {
        // Test across a range of maxLength values
        foreach (var maxLen in new[] { 20, 50, 100, 200, 500 })
        {
            var input = new string('z', maxLen + 100);
            var result = SensitiveContentSanitizer.Sanitize(input, maxLength: maxLen);

            result.Length.Should().BeLessThanOrEqualTo(maxLen,
                $"maxLength={maxLen}: total output must not exceed the contract");
        }
    }

    [Test]
    public void Sanitize_Truncation_VerySmallMaxLength_DoesNotCrash()
    {
        var input = new string('a', 100);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 5);

        result.Length.Should().BeLessThanOrEqualTo(5);
    }

    [Test]
    public void Sanitize_Truncation_MaxLengthEqualsSuffixLength_DoesNotCrash()
    {
        var input = new string('a', 100);
        // "...[truncated]" is 14 chars
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 14);

        result.Length.Should().BeLessThanOrEqualTo(14);
    }

    [Test]
    public void Sanitize_Truncation_MaxLengthJustAboveSuffix_IncludesSuffix()
    {
        var input = new string('a', 100);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 20);

        result.Length.Should().Be(20);
        result.Should().EndWith("...[truncated]");
    }

    [Test]
    public void Sanitize_NoTruncation_WhenUnderMaxLength()
    {
        var input = "Short safe message";
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 512);

        result.Should().Be(input);
    }

    [Test]
    public void Sanitize_NoTruncation_WhenExactlyAtMaxLength()
    {
        var input = new string('a', 512);
        var result = SensitiveContentSanitizer.Sanitize(input, maxLength: 512);

        result.Should().Be(input);
        result.Should().NotContain("[truncated]");
    }

    // ════════════════════════════════════════════════════════════════════
    // Combined scenarios
    // ════════════════════════════════════════════════════════════════════

    [Test]
    public void Sanitize_MixedNewPII_AllCaught()
    {
        var input = "Patient SSN 123456789, card 4111111111111111, phone 212-555-1234, " +
                    "email patient@hospital.org in INSERT INTO patients(data) VALUES('secret')";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().NotContain("123456789");
        result.Should().NotContain("4111111111111111");
        result.Should().NotContain("212-555-1234");
        result.Should().NotContain("patient@hospital.org");
        result.Should().NotContain("secret");
    }

    [Test]
    public void Sanitize_SafeOperationalMessage_FullyPreserved()
    {
        var input = "Retry attempt 3 of 5 failed after 30 seconds. Queue depth: 42.";
        var result = SensitiveContentSanitizer.Sanitize(input);

        result.Should().Be(input);
    }
}
