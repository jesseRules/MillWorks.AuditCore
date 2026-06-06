using System.Text.Json;
using Mapster;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Mapster configuration for audit entity mappings
/// </summary>
public sealed class AuditMappingConfiguration : IRegister
{
    /// <summary>
    /// Register mappings
    /// </summary>
    /// <param name="config"></param>
    public void Register(TypeAdapterConfig config)
    {
        // AuditEventEntity <-> AuditEventDto
        config.NewConfig<AuditEventEntity, AuditEventDto>()
            .Ignore(static dest => dest.Data!);

        config.NewConfig<AuditEventDto, AuditEventEntity>()
            .Ignore(static dest => dest.AuditIntegrity!);

        // AuditLogEntity <-> AuditLogDto
        config.NewConfig<AuditLogEntity, AuditLogDto>();
        config.NewConfig<AuditLogDto, AuditLogEntity>();

        // AuditIntegrityEntity <-> AuditIntegrityDto
        config.NewConfig<AuditIntegrityEntity, AuditIntegrityDto>();
        config.NewConfig<AuditIntegrityDto, AuditIntegrityEntity>()
            .Ignore(static dest => dest.AuditEvent!);

        // ArchiveRecordEntity -> ArchiveMetadata
        config.NewConfig<AuditArchiveRecordEntity, ArchiveMetadata>()
            .Map(static dest => dest.Status, static src => src.Status.ToString())
            .Map(static dest => dest.ArchiveHash, static src => src.Hash);

        // SecurityEventEntity <-> SecurityEventDto
        config.NewConfig<AuditSecurityEventEntity, SecurityEventDto>()
            .Map(static dest => dest.Details, static src => ParseDetailsJson(src.DetailsJson));

        config.NewConfig<SecurityEventDto, AuditSecurityEventEntity>()
            .Ignore(static dest => dest.DetailsJson!);

        // AuditEntry -> AuditEventEntity (for interceptor)
        config.NewConfig<AuditEntry, AuditEventEntity>()
            .Ignore(static dest => dest.EventId)
            .Map(static dest => dest.EventType, static src => $"{src.EntityName}.{src.Action}")
            .Map(static dest => dest.EntityType, static src => src.EntityName)
            .Map(static dest => dest.EntityId, static src =>
                src.KeyValues.FirstOrDefault().Value != null
                    ? src.KeyValues.FirstOrDefault().Value!.ToString()
                    : null)
            .Map(static dest => dest.Action, static src => src.Action)
            .Map(static dest => dest.UserId, static src => src.UserId)
            .Map(static dest => dest.AspNetUserId, static src => src.AspNetUserId)
            .Map(static dest => dest.InsertedDate, static src => DateTimeOffset.UtcNow)
            .Ignore(static dest => dest.AuditIntegrity!);
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