# Recursive Anonymization

**Status:** Deferred
**Origin:** ComplianceValidatorAccuracy.md finding #5
**Blocked By:** Coordinate with GDPR anonymization re-chaining
**Priority:** Medium

## Problem

`AuditComplianceService.AnonymizeJsonData` inspects only root-level properties. Nested PII is not detected or anonymized.

### Current Behavior

```csharp
// Input JsonData
{
    "Customer": {
        "Email": "john.doe@example.com",
        "FullName": "John Doe",
        "Address": {
            "Street": "123 Main St",
            "City": "Springfield"
        }
    },
    "Action": "ProfileUpdate"
}

// After AnonymizeJsonData (current)
{
    "Customer": {
        "Email": "john.doe@example.com",      // NOT anonymized
        "FullName": "John Doe",               // NOT anonymized
        "Address": {
            "Street": "123 Main St",          // NOT anonymized
            "City": "Springfield"
        }
    },
    "Action": "ProfileUpdate"
}
```

The method uses `property.WriteTo(writer)` for non-matching root properties, copying nested objects verbatim.

### GDPR Implication

For a GDPR erasure request, this under-deletes:
- Personal data remains in the system
- Erasure obligation is not fulfilled
- Potential regulatory violation

## Solution Approach

### Recursive Traversal

Mirror the `AuditCanonicalizer`'s recursive JSON traversal:

```csharp
private void AnonymizeJsonElement(
    JsonElement element, 
    Utf8JsonWriter writer,
    HashSet<string> piiFields,
    string currentPath = "")
{
    switch (element.ValueKind)
    {
        case JsonValueKind.Object:
            writer.WriteStartObject();
            foreach (var property in element.EnumerateObject())
            {
                var fieldPath = string.IsNullOrEmpty(currentPath) 
                    ? property.Name 
                    : $"{currentPath}.{property.Name}";
                
                writer.WritePropertyName(property.Name);
                
                if (IsPiiField(property.Name, piiFields))
                {
                    writer.WriteStringValue("[REDACTED]");
                }
                else
                {
                    AnonymizeJsonElement(property.Value, writer, piiFields, fieldPath);
                }
            }
            writer.WriteEndObject();
            break;
            
        case JsonValueKind.Array:
            writer.WriteStartArray();
            foreach (var item in element.EnumerateArray())
            {
                AnonymizeJsonElement(item, writer, piiFields, currentPath);
            }
            writer.WriteEndArray();
            break;
            
        default:
            element.WriteTo(writer);
            break;
    }
}
```

### PII Field Detection

Current heuristics check for common PII field names. These should also apply recursively:

```csharp
private static readonly HashSet<string> DefaultPiiFieldNames = new(StringComparer.OrdinalIgnoreCase)
{
    "email", "mail", "emailaddress",
    "name", "fullname", "firstname", "lastname", "givenname", "surname",
    "phone", "phonenumber", "mobile", "cell",
    "ssn", "socialsecuritynumber", "sin", "taxid",
    "address", "street", "city", "zip", "zipcode", "postalcode",
    "dob", "dateofbirth", "birthdate",
    "ip", "ipaddress",
    // ... etc
};

private bool IsPiiField(string fieldName, HashSet<string> additionalFields)
{
    return DefaultPiiFieldNames.Contains(fieldName) 
        || additionalFields.Contains(fieldName);
}
```

### Configuration

Allow consumers to specify:
1. Additional PII field names (beyond defaults)
2. Field paths to always anonymize (e.g., `"Customer.Email"`)
3. Field paths to never anonymize (overrides)
4. Maximum recursion depth (prevent stack overflow on malicious input)

```csharp
public class AnonymizationOptions
{
    public HashSet<string> AdditionalPiiFields { get; set; } = new();
    public HashSet<string> ExplicitAnonymizePaths { get; set; } = new();
    public HashSet<string> ExplicitPreservePaths { get; set; } = new();
    public int MaxDepth { get; set; } = 32;
}
```

## Design Considerations

### Array Handling

How to handle arrays of PII:

```json
{
    "Emails": ["john@example.com", "doe@example.com"],
    "Contacts": [
        {"Name": "John", "Phone": "555-1234"},
        {"Name": "Jane", "Phone": "555-5678"}
    ]
}
```

Options:
1. **Anonymize each element**: `["[REDACTED]", "[REDACTED]"]`
2. **Replace entire array**: `"Emails": "[REDACTED]"`
3. **Preserve structure, anonymize values**: Current approach for objects

Recommendation: Option 1 for primitive arrays, recurse into object arrays.

### Performance

Deep nesting with large documents could be expensive. Mitigations:
- `MaxDepth` limit (default 32)
- Stream-based processing (already using `Utf8JsonWriter`)
- Consider caching anonymization decisions per schema

### Determinism

For the same input and field list, anonymization must be deterministic — important for testing and for the integrity re-chaining (the new hash must be reproducible).

The current `"[REDACTED]"` marker is deterministic. Alternatives like random tokens would break re-verification.

## Coordination with Re-Chaining

This fix must ship together with `GdprAnonymizationReChaining.md`:

1. Anonymization modifies `JsonData` (this fix makes it recursive)
2. `EventHash` changes (computed from canonical JsonData)
3. Re-chaining records the supersession (GdprAnonymizationReChaining fix)

Without re-chaining, recursive anonymization still triggers false tamper alerts.

## Implementation Outline

1. Wait for Merkle pipeline design decisions (blocking via GdprAnonymizationReChaining)
2. Implement `AnonymizeJsonElement` recursive method
3. Add `AnonymizationOptions` configuration
4. Update `AnonymizeUserDataAsync` to use recursive method
5. Add tests:
   - Nested object PII anonymization
   - Array element anonymization
   - Max depth enforcement
   - Path-based overrides
6. Implement together with supersession record creation

## Test Cases

```csharp
[Fact]
public void AnonymizesNestedPii()
{
    var input = """{"Customer": {"Email": "test@example.com"}}""";
    var result = AnonymizeJsonData(input, new HashSet<string>());
    
    var doc = JsonDocument.Parse(result);
    var email = doc.RootElement
        .GetProperty("Customer")
        .GetProperty("Email")
        .GetString();
    
    Assert.Equal("[REDACTED]", email);
}

[Fact]
public void AnonymizesArrayElements()
{
    var input = """{"Emails": ["a@b.com", "c@d.com"]}""";
    var result = AnonymizeJsonData(input, new HashSet<string>());
    
    var doc = JsonDocument.Parse(result);
    var emails = doc.RootElement.GetProperty("Emails");
    
    Assert.All(emails.EnumerateArray(), e => Assert.Equal("[REDACTED]", e.GetString()));
}

[Fact]
public void RespectsMaxDepth()
{
    var deeplyNested = BuildDeeplyNestedJson(depth: 100);
    
    var ex = Assert.Throws<InvalidOperationException>(
        () => AnonymizeJsonData(deeplyNested, new HashSet<string>(), maxDepth: 32));
    
    Assert.Contains("Maximum depth", ex.Message);
}
```

## Related Documents

- `ComplianceValidatorAccuracy.md` — Origin finding
- `GdprAnonymizationReChaining.md` — Must implement together
- `TamperDetectionIntegrityGaps.md` — Integrity implications
