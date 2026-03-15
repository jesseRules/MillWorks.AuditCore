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
            .Ignore(static dest => dest.Details); // Parsed from DetailsJson

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
}