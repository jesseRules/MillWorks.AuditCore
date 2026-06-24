using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Explicit mappings between <see cref="AuditIntegrityEntity"/> and <see cref="AuditIntegrityDto"/>.
/// Replaces the former Mapster convention-based configuration.
/// </summary>
public static class AuditIntegrityMappings
{
    /// <summary>
    /// Maps an <see cref="AuditIntegrityEntity"/> to an <see cref="AuditIntegrityDto"/>.
    /// </summary>
    /// <param name="entity">Source entity.</param>
    /// <param name="includeEvent">
    /// When true (default), maps the <see cref="AuditIntegrityEntity.AuditEvent"/> navigation one
    /// level deep. The nested event is mapped without its back-reference to the integrity record,
    /// which breaks the EF relationship-fixup cycle.
    /// </param>
    public static AuditIntegrityDto ToDto(this AuditIntegrityEntity entity, bool includeEvent = true)
    {
        var dto = new AuditIntegrityDto
        {
            Id = entity.Id,
            EventId = entity.EventId,
            EventHash = entity.EventHash,
            PreviousEventHash = entity.PreviousEventHash,
            DigitalSignature = entity.DigitalSignature,
            TrustedTimestamp = entity.TrustedTimestamp,
            SequenceNumber = entity.SequenceNumber,
            HmacSignature = entity.HmacSignature,
            Checksum = entity.Checksum,
            AlgorithmVersion = entity.AlgorithmVersion,
            Parameters = entity.Parameters,
        };

        if (includeEvent && entity.AuditEvent is not null)
        {
            dto.AuditEvent = entity.AuditEvent.ToDto(includeIntegrity: false);
        }

        return dto;
    }

    /// <summary>
    /// Maps an <see cref="AuditIntegrityDto"/> to an <see cref="AuditIntegrityEntity"/>. The event
    /// navigation is intentionally not mapped (was Mapster <c>Ignore(dest.AuditEvent)</c>).
    /// </summary>
    public static AuditIntegrityEntity ToEntity(this AuditIntegrityDto dto)
    {
        var entity = new AuditIntegrityEntity
        {
            EventId = dto.EventId,
            PreviousEventHash = dto.PreviousEventHash,
            DigitalSignature = dto.DigitalSignature,
            HmacSignature = dto.HmacSignature,
            Parameters = dto.Parameters,
        };

        // Nullable source -> non-nullable destination: preserve the entity's constructor/initializer
        // default when the DTO carries no value rather than overwriting with default(T).
        if (dto.Id.HasValue) entity.Id = dto.Id.Value;
        if (dto.EventHash is not null) entity.EventHash = dto.EventHash;
        if (dto.TrustedTimestamp.HasValue) entity.TrustedTimestamp = dto.TrustedTimestamp.Value;
        if (dto.SequenceNumber.HasValue) entity.SequenceNumber = dto.SequenceNumber.Value;
        if (dto.Checksum is not null) entity.Checksum = dto.Checksum;
        if (dto.AlgorithmVersion.HasValue) entity.AlgorithmVersion = dto.AlgorithmVersion.Value;

        return entity;
    }
}
