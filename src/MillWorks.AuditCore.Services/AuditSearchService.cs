using System.Text.Json;
using MapsterMapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.Abstractions.Requests;
using MillWorks.AuditCore.Abstractions.Responses;
using MillWorks.AuditCore.EntityFramework.Data;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.Services.Interfaces;

namespace MillWorks.AuditCore.Services.Query;

/// <summary>
/// Audit search service for querying and retrieving audit events based on various criteria.
/// </summary>
/// <param name="context"></param>
/// <param name="mapper"></param>
/// <param name="logger"></param>
public sealed class AuditSearchService(
    AuditApplicationDbContext context,
    IMapper mapper,
    ILogger<AuditSearchService> logger)
    : IAuditSearchService
{
    /// <summary>
    /// Searches audit events based on the provided criteria in the request.
    /// </summary>
    /// <param name="request"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchAuditEventsAsync(
        AuditSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Searching audit events with criteria: {@Request}", request);

        IQueryable<AuditEventEntity> query = BuildSearchQuery(request);

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Skip(request.Offset)
            .Take(request.Limit)
            .ToListAsync(cancellationToken);

        List<AuditEventDto> items = MapAndEnrichAuditEvents(events);

        return new AuditEventsResponse
        {
            TotalItems = totalItems,
            Items = items,
            Offset = request.Offset,
            Limit = request.Limit,
            TotalPages = (int)Math.Ceiling((double)totalItems / request.Limit),
            CurrentPage = (request.Offset / request.Limit) + 1
        };
    }

    /// <summary>
    /// Gets a list of distinct users who have performed actions in the audit log.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<string>> GetDistinctUsersAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.User != null && e.User != "")
            .Select(static e => e.User!)
            .Distinct()
            .OrderBy(static u => u)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a list of distinct users who have performed actions in the audit log.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<string>> GetDistinctEventTypesAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.EventType != null && e.EventType != "")
            .Select(static e => e.EventType!)
            .Distinct()
            .OrderBy(static t => t)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets a list of distinct entity types that have been audited.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<string>> GetDistinctEntityTypesAsync(CancellationToken cancellationToken = default)
    {
        return await context.AuditEvents.AsNoTracking()
            .Where(static e => e.EntityType != null && e.EntityType != "")
            .Select(static e => e.EntityType!)
            .Distinct()
            .OrderBy(static t => t)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Searches audit events by entity type and optional entity ID.
    /// </summary>
    /// <param name="entityType"></param>
    /// <param name="entityId"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchByEntityAsync(
        string entityType,
        string? entityId = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        // Don't use SearchAuditEventsAsync - query by EntityType directly
        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking()
            .Where(e => e.EntityType == entityType);

        if (!string.IsNullOrEmpty(entityId))
        {
            query = query.Where(e => e.EntityId == entityId);
        }

        int totalItems = await query.CountAsync(cancellationToken);

        List<AuditEventEntity> events = await query
            .OrderByDescending(static e => e.InsertedDate)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(cancellationToken);

        List<AuditEventDto> items = MapAndEnrichAuditEvents(events);

        return new AuditEventsResponse
        {
            TotalItems = totalItems,
            Items = items,
            Offset = offset,
            Limit = limit,
            TotalPages = (int)Math.Ceiling((double)totalItems / limit),
            CurrentPage = (offset / limit) + 1
        };
    }

    /// <summary>
    /// Searches audit events by user and optional date range.
    /// </summary>
    /// <param name="username"></param>
    /// <param name="startDate"></param>
    /// <param name="endDate"></param>
    /// <param name="offset"></param>
    /// <param name="limit"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditEventsResponse> SearchByUserAsync(
        string username,
        DateTimeOffset? startDate = null,
        DateTimeOffset? endDate = null,
        int offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        AuditSearchRequest request = new AuditSearchRequest
        {
            User = username,
            StartDate = startDate,
            EndDate = endDate,
            Offset = offset,
            Limit = limit
        };

        return await SearchAuditEventsAsync(request, cancellationToken);
    }

    /// <summary>
    /// Builds the search query based on the provided request criteria.
    /// </summary>
    /// <param name="request"></param>
    /// <returns></returns>
    private IQueryable<AuditEventEntity> BuildSearchQuery(AuditSearchRequest request)
    {
        IQueryable<AuditEventEntity> query = context.AuditEvents.AsNoTracking();

        // Date range filters
        if (request.StartDate.HasValue)
        {
            query = query.Where(e => e.InsertedDate >= request.StartDate.Value);
        }

        if (request.EndDate.HasValue)
        {
            query = query.Where(e => e.InsertedDate <= request.EndDate.Value);
        }

        // User filter
        if (!string.IsNullOrWhiteSpace(request.User))
        {
            query = query.Where(e => e.User == request.User || e.UserFullName == request.User);
        }

        // Event type filter
        if (!string.IsNullOrWhiteSpace(request.EventType))
        {
            query = query.Where(e => e.EventType == request.EventType);
        }

        // General search term — use EF.Functions.Like for index-friendly case-insensitive search
        if (string.IsNullOrWhiteSpace(request.SearchTerm)) return query;
        {
            string pattern = $"%{request.SearchTerm}%";
            query = query.Where(e =>
                (e.JsonData != null && EF.Functions.Like(e.JsonData, pattern)) ||
                (e.User != null && EF.Functions.Like(e.User, pattern)) ||
                (e.UserFullName != null && EF.Functions.Like(e.UserFullName, pattern)) ||
                (e.EventType != null && EF.Functions.Like(e.EventType, pattern)) ||
                (e.EntityType != null && EF.Functions.Like(e.EntityType, pattern)) ||
                (e.EntityId != null && EF.Functions.Like(e.EntityId, pattern)) ||
                (e.AdditionalData != null && EF.Functions.Like(e.AdditionalData, pattern))
            );
        }

        return query;
    }

    /// <summary>
    /// Maps and enriches audit events by parsing their JSON data.
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    private List<AuditEventDto> MapAndEnrichAuditEvents(List<AuditEventEntity> events)
    {
        List<AuditEventDto> dtos = mapper.Map<List<AuditEventDto>>(events);

        foreach (AuditEventDto dto in dtos.Where(static d => !string.IsNullOrEmpty(d.JsonData)))
        {
            try
            {
                if (dto.JsonData == null)
                {
                    // Explicit null handling
                    dto.Data = null;
                    continue;
                }

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

                // Parse entity changes if present
                if (dto.Data.CustomFields != null)
                {
                    dto.Data.EntityChanges = ExtractEntityChanges(dto.Data.CustomFields);
                }
            }
            catch (JsonException jsonEx)
            {
                // Specific handling for JSON errors
                logger.LogError(jsonEx,
                    "Malformed JSON data for event {EventId}. JSON: {JsonData}",
                    dto.EventId,
                    dto.JsonData?[..Math.Min(100, dto.JsonData.Length)]);

                dto.Data = CreateErrorResponse(dto, "Malformed JSON", dto.JsonData);
            }
            catch (Exception ex)
            {
                // Generic error handling
                logger.LogError(ex,
                    "Unexpected error parsing event {EventId}",
                    dto.EventId);

                dto.Data = CreateErrorResponse(dto, "Parse error", dto.JsonData);
            }
        }

        return dtos;
    }

    /// <summary>
    /// Creates an error response for failed JSON parsing
    /// </summary>
    /// <param name="dto"></param>
    /// <param name="errorType"></param>
    /// <param name="rawJson"></param>
    /// <returns></returns>
    private static AuditEventJsonDataParsedResponse CreateErrorResponse(
        AuditEventDto dto,
        string errorType,
        string? rawJson)
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
                ["RawJson"] = rawJson,
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
    private AuditEnvironmentResponse? ParseEnvironment(JsonElement root)
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
    private AuditTargetResponse? ParseTarget(JsonElement root)
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
    /// Parses custom fields from the JSON element
    /// </summary>
    /// <param name="element"></param>
    /// <returns></returns>
    private Dictionary<string, object?>? ParseCustomFields(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        Dictionary<string, object?> customFields = new Dictionary<string, object?>();

        foreach (JsonProperty property in element.EnumerateObject())
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

        return customFields;
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
        if (fields.TryGetValue(primaryKey, out object? value) && value is T typedValue)
            return typedValue;

        if (fields.TryGetValue(alternateKey, out object? altValue) && altValue is T typedAltValue)
            return typedAltValue;

        // Try to deserialize if it's a JSON string
        if (fields.TryGetValue(primaryKey, out object? jsonValue) && jsonValue is string jsonStr)
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

        if (fields.TryGetValue(alternateKey, out object? altJsonValue) && altJsonValue is string altJsonStr)
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