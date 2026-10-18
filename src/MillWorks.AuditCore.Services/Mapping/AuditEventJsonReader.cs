using System.Text.Json;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Responses;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>Projects stored audit envelopes consistently for search and individual event reads.</summary>
internal sealed class AuditEventJsonReader(ILogger logger)
{
    internal AuditEventJsonDataParsedResponse? Read(AuditEventDto dto)
    {
        if (string.IsNullOrEmpty(dto.JsonData)) return null;
        try
        {
            using JsonDocument jsonDoc = JsonDocument.Parse(dto.JsonData);
            JsonElement root = jsonDoc.RootElement;

            dto.Data = new AuditEventJsonDataParsedResponse
            {
                EventId = TryGetGuid(root, "EventId", "event_id"),
                EventType = TryGetString(root, "EventType", "event_type"),
                StartDate = TryGetDateTimeOffset(root, "StartDate", "start_date"),
                EndDate = TryGetDateTimeOffset(root, "EndDate", "end_date"),
                Duration = TryGetInt32(root, "Duration", "duration"),
                Success = TryGetBoolean(root, "Success", "success"),
                ErrorMessage = TryGetString(root, "ErrorMessage", "error_message"),
                Environment = ParseEnvironment(root),
                Target = ParseTarget(root),
                CustomFields = ParseCustomFields(root)
            };

            // Preserve explicit response-shaped entity changes as well as stored custom-field changes.
            if (root.TryGetProperty("entity_changes", out JsonElement changes) &&
                changes.ValueKind == JsonValueKind.Object)
            {
                dto.Data.EntityChanges = changes.Deserialize<EntityChangesResponse>();
            }
            else if (dto.Data.CustomFields != null)
            {
                dto.Data.EntityChanges = ExtractEntityChanges(dto.Data.CustomFields);
            }
        }
        catch (JsonException jsonEx)
        {
            // Specific handling for JSON errors — do not log raw JSON (may contain sensitive data)
            logger.LogError(jsonEx,
                "Malformed JSON data for event {EventId}",
                dto.EventId);

            dto.Data = CreateErrorResponse(dto, "Malformed JSON");
        }
        catch (Exception ex)
        {
            // Generic error handling
            logger.LogError(ex,
                "Unexpected error parsing event {EventId}",
                dto.EventId);

            dto.Data = CreateErrorResponse(dto, "Parse error");
        }
        return dto.Data;
    }

    /// <summary>
    /// Creates an error response for failed JSON parsing
    /// </summary>
    /// <param name="dto"></param>
    /// <param name="errorType"></param>
    /// <returns></returns>
    private static AuditEventJsonDataParsedResponse CreateErrorResponse(
        AuditEventDto dto,
        string errorType)
    {
        return new AuditEventJsonDataParsedResponse
        {
            EventId = dto.EventId,
            EventType = dto.EventType,
            ErrorMessage = $"Failed to parse audit data: {errorType}",
            CustomFields = new Dictionary<string, object?>
            {
                ["ParseError"] = true,
                ["ErrorType"] = errorType,
                ["Timestamp"] = DateTimeOffset.UtcNow
            }
        };
    }


    /// <summary>
    /// Tries to get a GUID from the specified property names
    /// </summary>
    /// <param name="root"></param>
    /// <param name="propertyNames"></param>
    /// <returns></returns>
    private static Guid? TryGetGuid(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            if (!root.TryGetProperty(name, out JsonElement element))
                continue;

            // Handle null values
            if (element.ValueKind == JsonValueKind.Null)
                continue;

            // Try direct GUID parsing (if JSON contains a properly formatted GUID)
            if (element.TryGetGuid(out Guid guid))
            {
                return guid;
            }

            // Try parsing as string if it's a string value
            if (element.ValueKind != JsonValueKind.String) continue;
            string? stringValue = element.GetString();
            if (!string.IsNullOrEmpty(stringValue) && Guid.TryParse(stringValue, out Guid parsedGuid))
            {
                return parsedGuid;
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to get a string from the specified property names
    /// </summary>
    /// <param name="root"></param>
    /// <param name="propertyNames"></param>
    /// <returns></returns>
    private static string? TryGetString(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            if (!root.TryGetProperty(name, out JsonElement element))
                continue;

            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Null => null,
                // Stringify non-string values (e.g. Target.Old/New can be objects)
                _ => element.GetRawText()
            };
        }

        return null;
    }

    /// <summary>
    /// Tries to get a DateTimeOffset from the specified property names
    /// </summary>
    /// <param name="root"></param>
    /// <param name="propertyNames"></param>
    /// <returns></returns>
    private static DateTimeOffset? TryGetDateTimeOffset(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            if (root.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind != JsonValueKind.Null &&
                element.TryGetDateTimeOffset(out DateTimeOffset dateTime))
            {
                return dateTime;
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to get an Int32 from the specified property names
    /// </summary>
    /// <param name="root"></param>
    /// <param name="propertyNames"></param>
    /// <returns></returns>
    private static int? TryGetInt32(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            if (root.TryGetProperty(name, out JsonElement element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out int value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Tries to get an Int32 from the specified property names
    /// </summary>
    /// <param name="root"></param>
    /// <param name="propertyNames"></param>
    /// <returns></returns>
    private static bool? TryGetBoolean(JsonElement root, params string[] propertyNames)
    {
        foreach (string name in propertyNames)
        {
            if (root.TryGetProperty(name, out JsonElement element) &&
                (element.ValueKind == JsonValueKind.True ||
                 element.ValueKind == JsonValueKind.False))
            {
                return element.GetBoolean();
            }
        }

        return null;
    }

    /// <summary>
    /// Parses the environment information from the JSON element
    /// </summary>
    /// <param name="root"></param>
    /// <returns></returns>
    private static AuditEnvironmentResponse? ParseEnvironment(JsonElement root)
    {
        // Try to get Environment as a nested object first
        if (root.TryGetProperty("Environment", out JsonElement envElement) ||
            root.TryGetProperty("environment", out envElement))
        {
            if (envElement.ValueKind != JsonValueKind.Object)
                return null;

            return new AuditEnvironmentResponse
            {
                UserName = TryGetString(envElement, "UserName", "user_name"),
                MachineName = TryGetString(envElement, "MachineName", "machine_name"),
                DomainName = TryGetString(envElement, "DomainName", "domain_name"),
                CallingMethodName = TryGetString(envElement, "CallingMethodName", "calling_method_name"),
                AssemblyName = TryGetString(envElement, "AssemblyName", "assembly_name"),
                Culture = TryGetString(envElement, "Culture", "culture")
            };
        }

        return null;
    }

    /// <summary>
    /// Parses the environment information from the JSON element
    /// </summary>
    /// <param name="root"></param>
    /// <returns></returns>
    private static AuditTargetResponse? ParseTarget(JsonElement root)
    {
        // Try to get Target as a nested object first
        if (!root.TryGetProperty("Target", out JsonElement targetElement) &&
            !root.TryGetProperty("target", out targetElement)) return null;

        if (targetElement.ValueKind != JsonValueKind.Object)
            return null;

        return new AuditTargetResponse
        {
            Type = TryGetString(targetElement, "Type", "type"),
            Old = TryGetString(targetElement, "Old", "old"),
            New = TryGetString(targetElement, "New", "new")
        };
    }

    /// <summary>
    /// Parses custom fields from the JSON root element by looking for a nested CustomFields object.
    /// Does NOT enumerate all top-level properties (which would incorrectly include EventId, EventType, etc.).
    /// </summary>
    /// <param name="root">The root JSON element</param>
    /// <returns>The parsed CustomFields dictionary, or null if not present</returns>
    private static Dictionary<string, object?>? ParseCustomFields(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty("CustomFields", out JsonElement customFieldsElement) &&
            !root.TryGetProperty("custom_fields", out customFieldsElement))
        {
            return null;
        }

        if (customFieldsElement.ValueKind != JsonValueKind.Object)
            return null;

        var customFields = new Dictionary<string, object?>();

        foreach (JsonProperty property in customFieldsElement.EnumerateObject())
        {
            customFields[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out long longVal)
                    ? longVal
                    : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText()
            };
        }

        return customFields.Count > 0 ? customFields : null;
    }

    /// <summary>
    /// Extracts entity changes from custom fields
    /// </summary>
    /// <param name="customFields"></param>
    /// <returns></returns>
    private EntityChangesResponse? ExtractEntityChanges(Dictionary<string, object?> customFields)
    {
        if (!customFields.ContainsKey("EntityType") && !customFields.ContainsKey("entity_type"))
            return null;

        return new EntityChangesResponse
        {
            EntityType = GetFieldValue<string>(customFields, "EntityType", "entity_type"),
            EntityId = GetFieldValue<string>(customFields, "EntityId", "entity_id"),
            Action = GetFieldValue<string>(customFields, "Action", "action"),
            EntityState = GetFieldValue<string>(customFields, "EntityState", "entity_state"),
            KeyValues = GetFieldValue<Dictionary<string, object?>>(customFields, "KeyValues", "key_values"),
            ChangedProperties = GetFieldValue<List<string>>(customFields, "ChangedProperties", "changed_properties"),
            OldValues = GetFieldValue<Dictionary<string, object?>>(customFields, "OldValues", "old_values"),
            NewValues = GetFieldValue<Dictionary<string, object?>>(customFields, "NewValues", "new_values")
        };
    }

    /// <summary>
    /// Gets a field value from the dictionary, trying both primary and alternate keys
    /// </summary>
    /// <param name="fields"></param>
    /// <param name="primaryKey"></param>
    /// <param name="alternateKey"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    private T? GetFieldValue<T>(Dictionary<string, object?> fields, string primaryKey, string alternateKey)
        where T : class
    {
        if (fields.TryGetValue(primaryKey, out var value) && value is T typedValue)
            return typedValue;

        if (fields.TryGetValue(alternateKey, out var altValue) && altValue is T typedAltValue)
            return typedAltValue;

        // Try to deserialize if it's a JSON string
        if (fields.TryGetValue(primaryKey, out var jsonValue) && jsonValue is string jsonStr)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(jsonStr);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to deserialize field {FieldName} as {TypeName}",
                    primaryKey, typeof(T).Name);
            }
        }

        if (fields.TryGetValue(alternateKey, out var altJsonValue) && altJsonValue is string altJsonStr)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(altJsonStr);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to deserialize field {FieldName} as {TypeName}",
                    alternateKey, typeof(T).Name);
            }
        }

        return null;
    }
}
