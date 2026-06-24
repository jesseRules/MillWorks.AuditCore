using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;

namespace MillWorks.AuditCore.Services.Mapping;

/// <summary>
/// Explicit mapping from <see cref="AuditArchiveRecordEntity"/> to <see cref="ArchiveMetadata"/>.
/// Replaces the former Mapster convention-based configuration.
/// </summary>
public static class ArchiveMappings
{
    /// <summary>
    /// Maps an <see cref="AuditArchiveRecordEntity"/> to an <see cref="ArchiveMetadata"/> DTO.
    /// </summary>
    public static ArchiveMetadata ToMetadata(this AuditArchiveRecordEntity entity)
    {
        return new ArchiveMetadata
        {
            ArchiveId = entity.ArchiveId,
            ArchiveVersion = entity.ArchiveVersion,
            CompressionType = entity.CompressionType,
            // Custom rules carried over from the Mapster config.
            ArchiveHash = entity.Hash,
            Status = entity.Status.ToString(),
            CreatedAt = entity.CreatedAt,
            EventCount = entity.EventCount,
            DateRangeStart = entity.DateRangeStart,
            DateRangeEnd = entity.DateRangeEnd,
            SizeBytes = entity.SizeBytes,
        };
    }
}
