using MillWorks.AuditCore.Abstractions.Canonicalization;

namespace MillWorks.AuditCore.Tests.Canonicalization;

/// <summary>
/// Golden file tests for <see cref="AuditCanonicalizer"/>.
/// Each test uses a known JSON input and asserts the exact canonical output.
/// If an EF Core update, System.Text.Json change, or dependency bump alters
/// serialization behavior, these tests will fail — signaling investigation
/// before publishing a new NuGet package.
/// </summary>
[TestFixture]
[Category("GoldenFile")]
public class AuditCanonicalizerTests
{
    // ===== PROPERTY ORDERING =====

    [Test]
    public void SortsObjectPropertiesAlphabetically()
    {
        const string input = """{"zebra":1,"alpha":2,"mango":3}""";
        const string expected = """{"alpha":2,"mango":3,"zebra":1}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void SortsNestedObjectPropertiesRecursively()
    {
        const string input = """{"outer":{"z":1,"a":2},"inner":{"y":3,"b":4}}""";
        const string expected = """{"inner":{"b":4,"y":3},"outer":{"a":2,"z":1}}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void SortIsCaseSensitiveOrdinal()
    {
        // Ordinal: uppercase letters sort before lowercase
        const string input = """{"b":1,"A":2,"a":3,"B":4}""";
        const string expected = """{"A":2,"B":4,"a":3,"b":1}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== NULL HANDLING =====

    [Test]
    public void ExplicitNullsArePreserved()
    {
        const string input = """{"name":"test","value":null,"count":0}""";
        const string expected = """{"count":0,"name":"test","value":null}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void MultipleNullProperties()
    {
        const string input = """{"c":null,"a":null,"b":"present"}""";
        const string expected = """{"a":null,"b":"present","c":null}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== ARRAY HANDLING =====

    [Test]
    public void ArrayOrdinalOrderPreserved()
    {
        // ["B","A"] and ["A","B"] are semantically different — order must be preserved
        const string input = """{"items":["B","A","C"]}""";
        const string expected = """{"items":["B","A","C"]}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void EmptyArrayPreserved()
    {
        const string input = """{"tags":[]}""";
        const string expected = """{"tags":[]}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ArraysWithMixedTypes()
    {
        const string input = """{"data":[1,"two",true,null,3.14]}""";
        const string expected = """{"data":[1,"two",true,null,3.14]}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void ArrayOfObjects_ObjectsInternalPropertiesSorted()
    {
        const string input = """{"items":[{"z":1,"a":2},{"y":3,"b":4}]}""";
        const string expected = """{"items":[{"a":2,"z":1},{"b":4,"y":3}]}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void NestedArrays()
    {
        const string input = """{"matrix":[[3,1],[2,4]]}""";
        const string expected = """{"matrix":[[3,1],[2,4]]}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== DATE NORMALIZATION =====

    [Test]
    public void DateTimeOffset_ConvertedToUtcIso8601()
    {
        // Eastern time offset (-05:00) should be converted to UTC
        const string input = """{"created":"2026-03-12T10:30:00.0000000-05:00"}""";
        const string expected = """{"created":"2026-03-12T15:30:00.0000000Z"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void DateTimeUtc_PreservedAsUtc()
    {
        const string input = """{"created":"2026-03-12T15:30:00.0000000Z"}""";
        const string expected = """{"created":"2026-03-12T15:30:00.0000000Z"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void DateTimeWithFractionalSeconds_NormalizedToFullPrecision()
    {
        const string input = """{"timestamp":"2026-03-12T15:30:00.123Z"}""";
        const string expected = """{"timestamp":"2026-03-12T15:30:00.1230000Z"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void DateTimesAcrossTimeZones_AllNormalizedToSameUtc()
    {
        // Same instant expressed in different time zones
        var utc = """{"t":"2026-03-12T20:00:00.0000000Z"}""";
        var est = """{"t":"2026-03-12T15:00:00.0000000-05:00"}""";
        var jst = """{"t":"2026-03-13T05:00:00.0000000+09:00"}""";

        var resultUtc = AuditCanonicalizer.Canonicalize(utc);
        var resultEst = AuditCanonicalizer.Canonicalize(est);
        var resultJst = AuditCanonicalizer.Canonicalize(jst);

        // All should produce the same canonical output
        Assert.That(resultUtc, Is.EqualTo(resultEst));
        Assert.That(resultEst, Is.EqualTo(resultJst));
        Assert.That(resultUtc, Is.EqualTo("""{"t":"2026-03-12T20:00:00.0000000Z"}"""));
    }

    [Test]
    public void NonDateStrings_NotMisidentifiedAsDates()
    {
        const string input = """{"name":"hello world","code":"ABC-123"}""";
        const string expected = """{"code":"ABC-123","name":"hello world"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void NumericStrings_NotMisidentifiedAsDates()
    {
        // "2026" must NOT be rewritten as "2026-01-01T00:00:00.0000000Z"
        const string input = """{"version":"2026","count":"100","ratio":"3.14","time":"1:30"}""";
        const string expected = """{"count":"100","ratio":"3.14","time":"1:30","version":"2026"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void DateWithoutTimezoneOffset_WrittenVerbatim()
    {
        // No offset means ambiguous timezone — must NOT be converted to UTC
        const string input = """{"ts":"2026-03-12T10:30:00"}""";
        const string expected = """{"ts":"2026-03-12T10:30:00"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void SlashDates_NotMisidentifiedAsDates()
    {
        const string input = """{"value":"12/25/2026"}""";
        const string expected = """{"value":"12/25/2026"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== UNICODE NORMALIZATION =====

    [Test]
    public void UnicodeNormalization_ComposedAndDecomposedProduceSameOutput()
    {
        // é as single codepoint (NFC) vs e + combining acute accent (NFD)
        var nfc = "{\"name\":\"\u00e9\"}";
        var nfd = "{\"name\":\"e\u0301\"}";

        var resultNfc = AuditCanonicalizer.Canonicalize(nfc);
        var resultNfd = AuditCanonicalizer.Canonicalize(nfd);

        Assert.That(resultNfc, Is.EqualTo(resultNfd));
    }

    // ===== DEEPLY NESTED OBJECTS =====

    [Test]
    public void DeeplyNestedObject_AllLevelsSorted()
    {
        const string input = """{"level1":{"z":{"level3":{"c":1,"a":2}},"a":{"level3":{"b":3}}}}""";
        const string expected = """{"level1":{"a":{"level3":{"b":3}},"z":{"level3":{"a":2,"c":1}}}}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== WHITESPACE HANDLING =====

    [Test]
    public void WhitespaceStripped_CompactOutput()
    {
        const string input = """
        {
            "name" : "test" ,
            "value" : 42
        }
        """;
        const string expected = """{"name":"test","value":42}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== BOOLEAN AND NUMBER TYPES =====

    [Test]
    public void BooleanValues()
    {
        const string input = """{"active":true,"deleted":false}""";
        const string expected = """{"active":true,"deleted":false}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void IntegerValues()
    {
        const string input = """{"count":42,"negative":-7,"zero":0}""";
        const string expected = """{"count":42,"negative":-7,"zero":0}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void DecimalValues()
    {
        const string input = """{"price":19.99,"rate":0.05}""";
        const string expected = """{"price":19.99,"rate":0.05}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== EDGE CASES =====

    [Test]
    public void MalformedJson_ThrowsJsonException()
    {
        Assert.That(static () => AuditCanonicalizer.Canonicalize("{not valid json"),
            Throws.InstanceOf<System.Text.Json.JsonException>());
    }

    [Test]
    public void NullInput_ReturnsEmpty()
    {
        var result = AuditCanonicalizer.Canonicalize(null);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void EmptyInput_ReturnsEmpty()
    {
        var result = AuditCanonicalizer.Canonicalize(string.Empty);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void EmptyObject()
    {
        const string input = """{}""";
        const string expected = """{}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void TopLevelArray()
    {
        const string input = """[3,1,2]""";
        const string expected = """[3,1,2]""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    // ===== DETERMINISM =====

    [Test]
    public void SameInput_AlwaysProducesSameOutput()
    {
        const string input = """{"z":"2026-03-12T10:00:00-05:00","a":null,"m":[1,2,3]}""";

        var results = Enumerable.Range(0, 100)
            .Select(static _ => AuditCanonicalizer.Canonicalize(input))
            .Distinct()
            .ToList();

        Assert.That(results, Has.Count.EqualTo(1), "Canonicalization must be deterministic");
    }

    [Test]
    public void DifferentPropertyOrder_SameCanonicalOutput()
    {
        const string input1 = """{"b":2,"a":1,"c":3}""";
        const string input2 = """{"c":3,"a":1,"b":2}""";
        const string input3 = """{"a":1,"b":2,"c":3}""";

        var result1 = AuditCanonicalizer.Canonicalize(input1);
        var result2 = AuditCanonicalizer.Canonicalize(input2);
        var result3 = AuditCanonicalizer.Canonicalize(input3);

        Assert.That(result1, Is.EqualTo(result2));
        Assert.That(result2, Is.EqualTo(result3));
    }

    // ===== NORMALIZE DATE =====

    [Test]
    public void NormalizeDate_Null_ReturnsEmpty()
    {
        var result = AuditCanonicalizer.NormalizeDate(null);

        Assert.That(result, Is.EqualTo(string.Empty));
    }

    [Test]
    public void NormalizeDate_UtcOffset_ConvertedToUtc()
    {
        var dto = new DateTimeOffset(2026, 3, 12, 10, 30, 0, TimeSpan.FromHours(-5));

        var result = AuditCanonicalizer.NormalizeDate(dto);

        Assert.That(result, Is.EqualTo("2026-03-12T15:30:00.0000000Z"));
    }

    [Test]
    public void NormalizeDate_AlreadyUtc_Unchanged()
    {
        var dto = new DateTimeOffset(2026, 3, 12, 15, 30, 0, TimeSpan.Zero);

        var result = AuditCanonicalizer.NormalizeDate(dto);

        Assert.That(result, Is.EqualTo("2026-03-12T15:30:00.0000000Z"));
    }

    // ===== GOLDEN FILE: REALISTIC AUDIT EVENT JSON =====

    [Test]
    public void GoldenFile_RealisticAuditEventJson()
    {
        // Simulates a real audit event JsonData with properties in non-alphabetical order,
        // mixed types, nulls, nested objects, arrays, and dates across time zones
        const string input = """
        {
            "eventType": "User.Updated",
            "userId": "d3b07384-d9a4-4c7e-b6a0-9c1234567890",
            "timestamp": "2026-03-12T10:30:00.0000000-05:00",
            "changes": [
                {"property": "Email", "oldValue": "old@test.com", "newValue": "new@test.com"},
                {"property": "Name", "oldValue": null, "newValue": "Jane Doe"}
            ],
            "metadata": {
                "ipAddress": "192.168.1.1",
                "userAgent": null,
                "correlationId": "abc-123"
            },
            "tags": [],
            "success": true,
            "duration": 42
        }
        """;

        const string expected =
            """{"changes":[{"newValue":"new@test.com","oldValue":"old@test.com","property":"Email"},{"newValue":"Jane Doe","oldValue":null,"property":"Name"}],"duration":42,"eventType":"User.Updated","metadata":{"correlationId":"abc-123","ipAddress":"192.168.1.1","userAgent":null},"success":true,"tags":[],"timestamp":"2026-03-12T15:30:00.0000000Z","userId":"d3b07384-d9a4-4c7e-b6a0-9c1234567890"}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void GoldenFile_EntitySnapshotWithNavigationGuidsOnly()
    {
        // Simulates an entity snapshot that uses naked GUIDs instead of navigation properties
        const string input = """
        {
            "Id": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
            "CreatedById": "d3b07384-d9a4-4c7e-b6a0-9c1234567890",
            "UpdatedById": null,
            "Name": "Test Entity",
            "IsDeleted": false,
            "CreatedAt": "2026-01-15T08:00:00.0000000+00:00",
            "DeletedAt": null,
            "Tags": ["important", "reviewed"],
            "Score": 95.5
        }
        """;

        const string expected =
            """{"CreatedAt":"2026-01-15T08:00:00.0000000Z","CreatedById":"d3b07384-d9a4-4c7e-b6a0-9c1234567890","DeletedAt":null,"Id":"a1b2c3d4-e5f6-7890-abcd-ef1234567890","IsDeleted":false,"Name":"Test Entity","Score":95.5,"Tags":["important","reviewed"],"UpdatedById":null}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }

    [Test]
    public void GoldenFile_ComplexNestedWithMixedArrayTypes()
    {
        const string input = """
        {
            "report": {
                "sections": [
                    {
                        "title": "Summary",
                        "data": [100, "N/A", null, true],
                        "metadata": {"zOrder": 1, "aVisible": true}
                    }
                ],
                "generated": "2026-06-15T12:00:00.0000000+03:00"
            },
            "version": 3
        }
        """;

        const string expected =
            """{"report":{"generated":"2026-06-15T09:00:00.0000000Z","sections":[{"data":[100,"N/A",null,true],"metadata":{"aVisible":true,"zOrder":1},"title":"Summary"}]},"version":3}""";

        var result = AuditCanonicalizer.Canonicalize(input);

        Assert.That(result, Is.EqualTo(expected));
    }
}
