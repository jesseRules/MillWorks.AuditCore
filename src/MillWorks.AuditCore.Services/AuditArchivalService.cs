using System.IO.Compression;
using System.Text.Json;
using MapsterMapper;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MillWorks.AuditCore.Abstractions.Dto;
using MillWorks.AuditCore.EntityFramework.Dto;
using MillWorks.AuditCore.EntityFramework.Entities;
using MillWorks.AuditCore.EntityFramework.Repositories.Interfaces;
using MillWorks.AuditCore.Services.Interfaces;
using MillWorks.AuditCore.Services.TamperDetection.Interfaces;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// A service for archiving and restoring audit events using the complete repository system.
/// </summary>
/// <param name="auditEventRepository"></param>
/// <param name="auditIntegrityRepository"></param>
/// <param name="archiveRecordRepository"></param>
/// <param name="tamperDetectionService"></param>
/// <param name="blobServiceClient"></param>
/// <param name="mapper"></param>
/// <param name="logger"></param>
/// <param name="configuration"></param>
public sealed class AuditArchivalService(
    IAuditEventRepository auditEventRepository,
    IAuditIntegrityRepository auditIntegrityRepository,
    IArchiveRecordRepository archiveRecordRepository,
    IMapper mapper,
    ILogger<AuditArchivalService> logger,
    IConfiguration configuration,
    ITamperDetectionService? tamperDetectionService = null,
    BlobServiceClient? blobServiceClient = null)
    : IAuditArchivalService
{
    /// <summary>
    /// Container name for storing audit archives
    /// </summary>
    private readonly string _containerName = configuration["Audit:Archive:ContainerName"] ?? "audit-archives";

    /// <summary>
    /// Archives audit events older than the specified date.
    /// </summary>
    /// <param name="archiveBefore"></param>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    public async Task<AuditArchivalResult> ArchiveAuditEventsAsync(
        DateTimeOffset archiveBefore,
        string? archiveId = null, 
        CancellationToken cancellationToken = default)
    {
        // Check if archive already exists for this date range
        if (archiveId != null)
        {
            var existing = await archiveRecordRepository.GetByArchiveIdAsync(
                archiveId, cancellationToken);
            
            if (existing?.Status == MillWorksArchiveStatus.Completed)
            {
                return new AuditArchivalResult 
                { 
                    ArchiveId = archiveId,
                    Success = true,
                    Message = "Archive already exists"
                };
            }
        }
    
        var result = new AuditArchivalResult
        {
            ArchiveId = archiveId ?? Guid.NewGuid().ToString(),
            StartTime = DateTimeOffset.UtcNow
        };

        AuditArchiveRecordEntity? archiveRecord = null;

        try
        {
            // Create initial archive record
            archiveRecord = new AuditArchiveRecordEntity
            {
                ArchiveId = result.ArchiveId,
                BlobName = $"audit-archive-{result.ArchiveId}.gz",
                ContainerName = _containerName,
                Status = MillWorksArchiveStatus.InProgress
            };

            await archiveRecordRepository.AddAsync(archiveRecord, cancellationToken);
            await archiveRecordRepository.SaveChangesAsync(cancellationToken);

            // Begin transaction for consistency
            var transaction = await auditEventRepository.BeginTransactionAsync(cancellationToken);

            try
            {
                // Get events to archive
                IEnumerable<AuditEventDto> eventsToArchive =
                    await GetEventsToArchiveAsync(archiveBefore, cancellationToken);
                List<AuditEventDto> eventsList = eventsToArchive.ToList();

                if (!eventsList.Any())
                {
                    result.Message = "No events to archive";
                    await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId, MillWorksArchiveStatus.Completed,
                        "No events found to archive", cancellationToken);
                    await transaction.RollbackAsync(cancellationToken);
                    return result;
                }

                result.EventCount = eventsList.Count;

                // Verify integrity before archiving
                logger.LogInformation("Verifying integrity of {Count} events before archival", eventsList.Count);

                foreach (var evt in eventsList)
                {
                    if (!evt.EventId.HasValue || evt.EventId == Guid.Empty)
                    {
                        logger.LogError("Event with empty EventId found, aborting archive");
                        await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId, MillWorksArchiveStatus.Failed,
                            "Event with empty EventId found", cancellationToken);
                        throw new InvalidOperationException("Cannot archive - event with empty EventId found");
                    }

                    if (tamperDetectionService is not null)
                    {
                        var isValid =
                            await tamperDetectionService.VerifyIntegrityAsync(evt.EventId.Value, cancellationToken);
                        if (!isValid)
                        {
                            logger.LogError("Event {EventId} failed integrity check, aborting archive", evt.EventId);
                            await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId,
                                MillWorksArchiveStatus.Failed,
                                $"Event {evt.EventId} integrity check failed", cancellationToken);
                            throw new InvalidOperationException(
                                $"Cannot archive - event {evt.EventId} integrity check failed");
                        }
                    }
                }

                if (tamperDetectionService is null)
                {
                    logger.LogWarning(
                        "Tamper detection is not configured — archiving {Count} events without integrity verification",
                        eventsList.Count);
                }

                // Get integrity records for events to archive
                var eventEntities = mapper.Map<List<AuditEventEntity>>(eventsList);
                var integrityRecords = await GetIntegrityRecordsForEventsAsync(eventEntities, cancellationToken);
                var integrityList = integrityRecords.ToList();

                // Update archive record with event details
                archiveRecord.EventCount = eventsList.Count;
                archiveRecord.DateRangeStart = eventsList.Min(static e => e.InsertedDate) ?? DateTimeOffset.MinValue;
                archiveRecord.DateRangeEnd = eventsList.Max(static e => e.InsertedDate) ?? DateTimeOffset.MaxValue;

                // Create archive
                var archive = new AuditArchive
                {
                    ArchiveId = result.ArchiveId,
                    CreatedAt = DateTimeOffset.UtcNow,
                    DateRangeStart = archiveRecord.DateRangeStart,
                    DateRangeEnd = archiveRecord.DateRangeEnd,
                    EventCount = eventsList.Count,
                    Events = eventsList,
                    IntegrityRecords = mapper.Map<List<AuditIntegrityDto>>(integrityList),
                    Metadata = new ArchiveMetadata
                    {
                        ArchiveId = result.ArchiveId,
                        ArchiveVersion = "1.0",
                        CompressionType = "gzip",
                        ArchiveHash = ComputeArchiveHash(eventsList),
                        CreatedAt = DateTimeOffset.UtcNow,
                        EventCount = eventsList.Count,
                        DateRangeStart = archiveRecord.DateRangeStart,
                        DateRangeEnd = archiveRecord.DateRangeEnd,
                        Status = "InProgress"
                    }
                };

                // Serialize and compress
                var archiveJson = JsonSerializer.Serialize(archive, new JsonSerializerOptions
                {
                    WriteIndented = false,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                });

                var compressedData = await CompressDataAsync(archiveJson);
                var archiveHash = ComputeBytesHash(compressedData);

                // Upload to blob storage
                if (blobServiceClient is null)
                {
                    throw new InvalidOperationException(
                        "Blob storage client is not configured. Archive storage requires UseArchival() with ArchivalProvider.AzureBlob and a connection string.");
                }

                var containerClient = blobServiceClient.GetBlobContainerClient(_containerName);
                await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);

                var blobClient = containerClient.GetBlobClient(archiveRecord.BlobName);

                using var stream = new MemoryStream(compressedData);
                await blobClient.UploadAsync(stream, cancellationToken);

                // Update archive record with final details
                archiveRecord.SizeBytes = compressedData.Length;
                archiveRecord.Hash = archiveHash;
                archiveRecord.Status = MillWorksArchiveStatus.Completed;

                await archiveRecordRepository.UpdateAsync(archiveRecord, cancellationToken);

                // Delete archived events from main table
                await auditEventRepository.DeleteRangeAsync(eventEntities, cancellationToken);

                // Delete associated integrity records
                await auditIntegrityRepository.DeleteRangeAsync(integrityList, cancellationToken);

                // Save changes and commit transaction
                await auditEventRepository.SaveChangesAsync(cancellationToken);
                await auditIntegrityRepository.SaveChangesAsync(cancellationToken);
                await archiveRecordRepository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                result.Success = true;
                result.EndTime = DateTimeOffset.UtcNow;
                result.Message = $"Successfully archived {eventsList.Count} events";
                result.ArchiveSize = compressedData.Length;

                logger.LogInformation("Archive completed: {ArchiveId} with {Count} events",
                    result.ArchiveId, result.EventCount);

                // Emit an audit event for the completed archive (STIG AU-9(2) compliance).
                // Wrapped in its own try/catch so a failure here doesn't mark the
                // already-committed archive as failed.
                try
                {
                    var archiveAuditEvent = new AuditEventEntity
                    {
                        EventType = "Audit.Archived",
                        Action = "Added",
                        InsertedDate = DateTimeOffset.UtcNow,
                        User = "system",
                        EntityType = "AuditArchiveRecord",
                        EntityId = result.ArchiveId,
                        AdditionalData = JsonSerializer.Serialize(new
                        {
                            result.ArchiveId,
                            result.EventCount,
                            result.ArchiveSize,
                            archiveRecord.DateRangeStart,
                            archiveRecord.DateRangeEnd,
                            archiveRecord.ContainerName,
                            archiveRecord.BlobName
                        })
                    };

                    await auditEventRepository.AddAsync(archiveAuditEvent, cancellationToken);
                    await auditEventRepository.SaveChangesAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to emit audit event for archive {ArchiveId} — archive itself completed successfully",
                        result.ArchiveId);
                }

                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive operation failed");

            // Update archive record status to failed if it was created
            if (archiveRecord != null)
            {
                await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId, MillWorksArchiveStatus.Failed,
                    ex.Message, cancellationToken);
            }

            result.Success = false;
            result.Message = $"Archive failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Restores archived audit events by their archive ID.
    /// </summary>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<AuditRestoreResult> RestoreArchivedEventsAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        var result = new AuditRestoreResult { ArchiveId = archiveId };

        try
        {
            // Get archive metadata from repository
            var archiveRecord = await archiveRecordRepository.GetByArchiveIdAsync(archiveId, cancellationToken);

            if (archiveRecord == null)
            {
                result.Message = "Archive not found";
                return result;
            }

            if (archiveRecord.Status != MillWorksArchiveStatus.Completed)
            {
                result.Message = $"Archive is not in completed state. Current status: {archiveRecord.Status}";
                return result;
            }

            // Download from blob storage
            if (blobServiceClient is null)
            {
                result.Message = "Blob storage client is not configured. Restore requires UseArchival() with ArchivalProvider.AzureBlob.";
                return result;
            }

            var containerClient = blobServiceClient.GetBlobContainerClient(archiveRecord.ContainerName);
            var blobClient = containerClient.GetBlobClient(archiveRecord.BlobName);

            if (!await blobClient.ExistsAsync(cancellationToken))
            {
                result.Message = "Archive blob not found";
                await archiveRecordRepository.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                    "Blob file missing", cancellationToken);
                return result;
            }

            // Download and decompress
            using var downloadStream = new MemoryStream();
            await blobClient.DownloadToAsync(downloadStream, cancellationToken);
            var compressedBytes = downloadStream.ToArray();

            var decompressedData = await DecompressDataAsync(compressedBytes);
            var archiveJson = System.Text.Encoding.UTF8.GetString(decompressedData);

            var archive = JsonSerializer.Deserialize<AuditArchive>(archiveJson);
            if (archive == null)
            {
                result.Message = "Failed to deserialize archive";
                await archiveRecordRepository.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                    "Failed to deserialize archive data", cancellationToken);
                return result;
            }

            // Verify archive integrity using the same hash method as archival (compressed bytes)
            var computedHash = ComputeBytesHash(compressedBytes);
            if (computedHash != archiveRecord.Hash)
            {
                logger.LogError("Archive {ArchiveId} integrity check failed", archiveId);
                result.Message = "Archive integrity check failed";
                await archiveRecordRepository.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                    "Hash verification failed", cancellationToken);
                return result;
            }

            // Restore events to database using repositories
            var transaction = await auditEventRepository.BeginTransactionAsync(cancellationToken);

            try
            {
                foreach (var evt in archive.Events)
                {
                    // Reset navigation properties to avoid conflicts
                    evt.AuditIntegrity = null;
                    AuditEventEntity auditEvent = mapper.Map<AuditEventEntity>(evt);
                    await auditEventRepository.AddAsync(auditEvent, cancellationToken);
                }

                // Restore integrity records
                foreach (var integrity in archive.IntegrityRecords)
                {
                    integrity.AuditEvent = null;
                    AuditIntegrityEntity auditEvent = new AuditIntegrityEntity
                    {
                        EventId = integrity.EventId,
                        AuditEvent = null
                    };

                    await auditIntegrityRepository.AddAsync(auditEvent, cancellationToken);
                }

                await auditEventRepository.SaveChangesAsync(cancellationToken);
                await auditIntegrityRepository.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                result.Success = true;
                result.RestoredEventCount = archive.Events.Count;
                result.Message = $"Successfully restored {archive.Events.Count} events";

                logger.LogInformation("Restore completed: {ArchiveId} with {Count} events",
                    archiveId, archive.Events.Count);

                return result;
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken);
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Restore operation failed for archive {ArchiveId}", archiveId);
            result.Message = $"Restore failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Gets a list of all archived audit events with their metadata.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<List<ArchiveMetadata>> GetArchivesAsync(CancellationToken cancellationToken = default)
    {
        var archiveRecords = await archiveRecordRepository.GetAllOrderedAsync(cancellationToken);
        return mapper.Map<List<ArchiveMetadata>>(archiveRecords);
    }

    /// <summary>
    /// Validates the integrity of an archived audit event by its archive ID.
    /// </summary>
    /// <param name="archiveId"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    public async Task<bool> ValidateArchiveIntegrityAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var archiveRecord = await archiveRecordRepository
                .GetByArchiveIdAsync(archiveId, cancellationToken);

            if (archiveRecord == null)
            {
                logger.LogWarning("Archive record {ArchiveId} not found", archiveId);
                return false;
            }

            if (archiveRecord.Status != MillWorksArchiveStatus.Completed)
            {
                logger.LogWarning("Archive {ArchiveId} is not in completed state: {Status}",
                    archiveId, archiveRecord.Status);
                return false;
            }

            // Download blob
            if (blobServiceClient is null)
            {
                logger.LogError("Blob storage client is not configured — cannot validate archive {ArchiveId}", archiveId);
                return false;
            }

            var containerClient = blobServiceClient.GetBlobContainerClient(archiveRecord.ContainerName);
            var blobClient = containerClient.GetBlobClient(archiveRecord.BlobName);

            if (!await blobClient.ExistsAsync(cancellationToken))
            {
                logger.LogError("Archive blob {BlobName} not found", archiveRecord.BlobName);
                await archiveRecordRepository.UpdateStatusAsync(archiveId,
                    MillWorksArchiveStatus.Corrupted, "Blob file missing", cancellationToken);
                return false;
            }

            // Hash the RAW compressed bytes, not decompressed/deserialized data
            using var downloadStream = new MemoryStream();
            await blobClient.DownloadToAsync(downloadStream, cancellationToken);
            var compressedBytes = downloadStream.ToArray();

            // Compute hash of the actual file content
            var computedHash = ComputeBytesHash(compressedBytes);
            var isValid = computedHash == archiveRecord.Hash;

            if (isValid)
            {
                // Update verification timestamp
                await archiveRecordRepository.UpdateVerificationTimestampAsync(
                    archiveId, DateTimeOffset.UtcNow, cancellationToken);

                logger.LogDebug("Archive {ArchiveId} integrity verification passed", archiveId);
            }
            else
            {
                logger.LogError(
                    "Archive {ArchiveId} integrity verification failed. Expected: {Expected}, Actual: {Actual}",
                    archiveId, archiveRecord.Hash, computedHash);

                await archiveRecordRepository.UpdateStatusAsync(archiveId,
                    MillWorksArchiveStatus.Corrupted, "Hash verification failed", cancellationToken);
            }

            return isValid;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to validate archive {ArchiveId}", archiveId);
            return false;
        }
    }


    /// <summary>
    /// Gets audit events to be archived before the specified date.
    /// </summary>
    /// <param name="archiveBefore"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IEnumerable<AuditEventDto>> GetEventsToArchiveAsync(
        DateTimeOffset archiveBefore,
        CancellationToken cancellationToken)
    {
        var events = await auditEventRepository.FindAsync(
            e => e.InsertedDate < archiveBefore, cancellationToken);
        var ordered = events.OrderBy(static e => e.InsertedDate);

        return mapper.Map<IEnumerable<AuditEventDto>>(ordered);
    }

    /// <summary>
    /// Computes the SHA256 hash of the given byte array and returns it as a Base64 string.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private static string ComputeBytesHash(byte[] data)
    {
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(data);
        return Convert.ToBase64String(hashBytes);
    }

    /// <summary>
    /// Gets integrity records for the specified audit events.
    /// </summary>
    /// <param name="events"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private async Task<IEnumerable<AuditIntegrityEntity>> GetIntegrityRecordsForEventsAsync(
        IEnumerable<AuditEventEntity> events,
        CancellationToken cancellationToken)
    {
        var eventIds = events.Select(static e => e.EventId).ToList();
        var integrityRecords = new List<AuditIntegrityEntity>();

        foreach (var eventId in eventIds)
        {
            var integrity = await auditIntegrityRepository.GetByEventIdAsync(eventId, cancellationToken);
            if (integrity != null)
            {
                integrityRecords.Add(integrity);
            }
        }

        return integrityRecords;
    }

    /// <summary>
    /// Compresses the given string data using GZip compression.
    /// </summary>
    /// <param name="data"></param>
    /// <returns></returns>
    private async Task<byte[]> CompressDataAsync(string data)
    {
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(data);
            await gzip.WriteAsync(bytes, 0, bytes.Length);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Decompresses the given GZip compressed byte array.
    /// </summary>
    /// <param name="compressedData"></param>
    /// <returns></returns>
    private async Task<byte[]> DecompressDataAsync(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            await gzip.CopyToAsync(output);
        }

        return output.ToArray();
    }

    /// <summary>
    /// Computes a hash for the archive based on its events.
    /// </summary>
    /// <param name="events"></param>
    /// <returns></returns>
    private string ComputeArchiveHash(IEnumerable<AuditEventDto> events)
    {
        var dataToHash = string.Join("|", events.Select(static e => $"{e.EventId}:{e.EventType}:{e.InsertedDate}"));
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        var hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(dataToHash));
        return Convert.ToBase64String(hashBytes);
    }
}