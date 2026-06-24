using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Explicit mappings between <see cref="AuditLogEntity"/> and <see cref="AuditLogDto"/>.
/// Replaces the former Mapster convention-based configuration.
/// </summary>
public static class AuditLogMappings
{
    /// <summary>
    /// Maps an <see cref="AuditLogEntity"/> to an <see cref="AuditLogDto"/>.
    /// </summary>
    public static AuditLogDto ToDto(this AuditLogEntity entity)
    {
        return new AuditLogDto
        {
            Id = entity.Id,
            EntityName = entity.EntityName,
            // Nullable entity key projected onto a non-nullable DTO field (matches the previous
            // Mapster behavior of substituting default(Guid) when the source is null).
            EntityId = entity.EntityId ?? Guid.Empty,
            Action = entity.Action,
            PropertyName = entity.PropertyName,
            OldValue = entity.OldValue,
            NewValue = entity.NewValue,
            Description = entity.Description,
            AdditionalData = entity.AdditionalData,
            CorrelationId = entity.CorrelationId,
            UserAgent = entity.UserAgent,
            IpAddress = entity.IpAddress,
            CreatedAt = entity.CreatedAt,
            CreatedById = entity.CreatedById,
        };
    }

    /// <summary>
    /// Maps an <see cref="AuditLogDto"/> to an <see cref="AuditLogEntity"/>.
    /// <see cref="AuditLogEntity.EnvelopeId"/> has no DTO counterpart and is left unset.
    /// </summary>
    public static AuditLogEntity ToEntity(this AuditLogDto dto)
    {
        return new AuditLogEntity
        {
            Id = dto.Id,
            EntityName = dto.EntityName,
            EntityId = dto.EntityId,
            Action = dto.Action,
            PropertyName = dto.PropertyName,
            OldValue = dto.OldValue,
            NewValue = dto.NewValue,
            Description = dto.Description,
            AdditionalData = dto.AdditionalData,
            CorrelationId = dto.CorrelationId,
            UserAgent = dto.UserAgent,
            IpAddress = dto.IpAddress,
            CreatedAt = dto.CreatedAt,
            CreatedById = dto.CreatedById,
        };
    }
}
