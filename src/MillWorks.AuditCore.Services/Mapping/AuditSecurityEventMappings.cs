using System.Text.Json;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Explicit mappings between <see cref="AuditSecurityEventEntity"/> and <see cref="SecurityEventDto"/>.
/// Replaces the former Mapster convention-based configuration.
/// </summary>
public static class AuditSecurityEventMappings
{
    /// <summary>
    /// Maps an <see cref="AuditSecurityEventEntity"/> to a <see cref="SecurityEventDto"/>,
    /// parsing the stored <see cref="AuditSecurityEventEntity.DetailsJson"/> into the
    /// <see cref="SecurityEventDto.Details"/> dictionary.
    /// </summary>
    public static SecurityEventDto ToDto(this AuditSecurityEventEntity entity)
    {
        return new SecurityEventDto
        {
            Id = entity.Id,
            EventType = entity.EventType,
            Severity = entity.Severity,
            RelatedAuditEventId = entity.RelatedAuditEventId,
            Message = entity.Message,
            Details = ParseDetailsJson(entity.DetailsJson),
            DetectedAt = entity.DetectedAt,
            DetectedBy = entity.DetectedBy,
            IpAddress = entity.IpAddress,
            CorrelationId = entity.CorrelationId,
            TenantId = entity.TenantId,
            ActorUserId = entity.ActorUserId,
            SubjectUserId = entity.SubjectUserId,
            SourceIpHash = entity.SourceIpHash,
            UserAgentHash = entity.UserAgentHash,
            Operation = entity.Operation,
            Status = entity.Status,
            ResolvedAt = entity.ResolvedAt,
            ResolvedBy = entity.ResolvedBy,
            Resolution = entity.Resolution,
        };
    }

    /// <summary>
    /// Maps a <see cref="SecurityEventDto"/> to an <see cref="AuditSecurityEventEntity"/>.
    /// <see cref="AuditSecurityEventEntity.DetailsJson"/> is intentionally not set here (was
    /// Mapster <c>Ignore</c>): the recording path serializes <see cref="SecurityEventDto.Details"/>
    /// into <c>DetailsJson</c> itself with its own size guard and truncation-summary fallback.
    /// </summary>
    public static AuditSecurityEventEntity ToEntity(this SecurityEventDto dto)
    {
        return new AuditSecurityEventEntity
        {
            Id = dto.Id,
            EventType = dto.EventType,
            Severity = dto.Severity,
            RelatedAuditEventId = dto.RelatedAuditEventId,
            Message = dto.Message,
            DetectedAt = dto.DetectedAt,
            DetectedBy = dto.DetectedBy,
            IpAddress = dto.IpAddress,
            CorrelationId = dto.CorrelationId,
            TenantId = dto.TenantId,
            ActorUserId = dto.ActorUserId,
            SubjectUserId = dto.SubjectUserId,
            SourceIpHash = dto.SourceIpHash,
            UserAgentHash = dto.UserAgentHash,
            Operation = dto.Operation,
            Status = dto.Status,
            ResolvedAt = dto.ResolvedAt,
            ResolvedBy = dto.ResolvedBy,
            Resolution = dto.Resolution,
        };
    }

    /// <summary>
    /// Parses DetailsJson back to a dictionary with safe malformed-JSON handling.
    /// Returns an empty dictionary on null, empty, or invalid JSON.
    /// </summary>
    private static Dictionary<string, object?> ParseDetailsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new Dictionary<string, object?>();

        try
        {
            var result = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json);
            if (result is null)
                return new Dictionary<string, object?>();

            var dict = new Dictionary<string, object?>();
            foreach (var kvp in result)
            {
                dict[kvp.Key] = ConvertJsonElement(kvp.Value);
            }
            return dict;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }

    /// <summary>
    /// Converts a JsonElement to a primitive or collection type for round-trip compatibility.
    /// </summary>
    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(static p => p.Name, static p => ConvertJsonElement(p.Value)),
            _ => element.GetRawText()
        };
    }
}
