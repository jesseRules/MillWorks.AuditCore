using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Explicit mappings between <see cref="AuditEventEntity"/> and <see cref="AuditEventDto"/>.
/// Replaces the former Mapster convention-based configuration; every property the previous
/// configuration mapped by name is set here explicitly.
/// </summary>
public static class AuditEventMappings
{
    /// <summary>
    /// Maps an <see cref="AuditEventEntity"/> to an <see cref="AuditEventDto"/>.
    /// </summary>
    /// <param name="entity">Source entity.</param>
    /// <param name="includeIntegrity">
    /// When true (default), maps the <see cref="AuditEventEntity.AuditIntegrity"/> navigation one
    /// level deep. The nested integrity record is mapped without its back-reference to the event,
    /// which breaks the EF relationship-fixup cycle (loading an event with its integrity record
    /// populates <c>integrity.AuditEvent</c> back to the same event instance).
    /// </param>
    public static AuditEventDto ToDto(this AuditEventEntity entity, bool includeIntegrity = true)
    {
        var dto = new AuditEventDto
        {
            EventId = entity.EventId,
            InsertedDate = entity.InsertedDate,
            LastUpdatedDate = entity.LastUpdatedDate,
            JsonData = entity.JsonData,
            EventType = entity.EventType,
            User = entity.User,
            UserEnvName = entity.UserEnvName,
            EntityId = entity.EntityId,
        };

        if (includeIntegrity && entity.AuditIntegrity is not null)
        {
            dto.AuditIntegrity = entity.AuditIntegrity.ToDto(includeEvent: false);
        }

        return dto;
    }

    /// <summary>
    /// Maps an <see cref="AuditEventDto"/> to an <see cref="AuditEventEntity"/>. The integrity
    /// navigation is intentionally not mapped (was Mapster <c>Ignore(dest.AuditIntegrity)</c>).
    /// </summary>
    public static AuditEventEntity ToEntity(this AuditEventDto dto)
    {
        var entity = new AuditEventEntity
        {
            InsertedDate = dto.InsertedDate,
            LastUpdatedDate = dto.LastUpdatedDate,
            JsonData = dto.JsonData,
            EventType = dto.EventType,
            User = dto.User,
            UserEnvName = dto.UserEnvName,
            EntityId = dto.EntityId,
        };

        // EventId is the primary key; the entity initializes a fresh Guid in its field initializer.
        // Preserve that default when the source DTO carries no value rather than zeroing the key.
        if (dto.EventId.HasValue)
        {
            entity.EventId = dto.EventId.Value;
        }

        return entity;
    }
}
