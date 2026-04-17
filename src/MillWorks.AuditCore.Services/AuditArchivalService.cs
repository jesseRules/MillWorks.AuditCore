using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using MapsterMapper;
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
/// Archival streams events from the database into a gzip-compressed JSON document that is
/// uploaded directly to blob storage, so peak memory is bounded by the block-upload buffer
/// rather than the total archive size.
/// </summary>
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
    /// Pipe back-pressure threshold. Once this many unconsumed bytes are buffered in the
    /// producer → consumer pipe, the producer pauses until the uploader drains the buffer.
    /// Bounds peak memory of the streaming pipeline independent of archive size.
    /// </summary>
    private const int PipePauseBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How often the JSON writer's internal buffer is flushed to the gzip stream during
    /// event streaming. Keeps the writer's own buffer bounded independent of event count.
    /// </summary>
    private const int JsonFlushEveryNEvents = 500;

    private static readonly JsonSerializerOptions StreamJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly byte[] EventsHashSeparator = "|"u8.ToArray();

    /// <summary>
    /// Container name for storing audit archives
    /// </summary>
    private readonly string _containerName = configuration["Audit:Archive:ContainerName"] ?? "audit-archives";

    /// <summary>
    /// Archives audit events older than the specified date by streaming them into a gzip
    /// payload uploaded to blob storage. Integrity verification (if configured) happens
    /// inline as each event is streamed.
    /// </summary>
    public async Task<AuditArchivalResult> ArchiveAuditEventsAsync(
        DateTimeOffset archiveBefore,
        string? archiveId = null,
        CancellationToken cancellationToken = default)
    {
        if (archiveId != null)
        {
            var existing = await archiveRecordRepository.GetByArchiveIdAsync(archiveId, cancellationToken);

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

        if (blobServiceClient is null)
        {
            logger.LogError("Blob storage client is not configured — cannot archive events");
            result.Message =
                "Blob storage client is not configured. Archive storage requires UseArchival() with ArchivalProvider.AzureBlob and a connection string.";
            return result;
        }

        AuditArchiveRecordEntity? archiveRecord = null;
        BlobClient? blobClient = null;
        var blobWriteStarted = false;

        try
        {
            archiveRecord = new AuditArchiveRecordEntity
            {
                ArchiveId = result.ArchiveId,
                BlobName = $"audit-archive-{result.ArchiveId}.gz",
                ContainerName = _containerName,
                Status = MillWorksArchiveStatus.InProgress
            };

            await archiveRecordRepository.AddAsync(archiveRecord, cancellationToken);
            await archiveRecordRepository.SaveChangesAsync(cancellationToken);

            var eventCount = await auditEventRepository.CountAsync(
                e => e.InsertedDate < archiveBefore, cancellationToken);

            if (eventCount == 0)
            {
                result.Message = "No events to archive";
                await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId,
                    MillWorksArchiveStatus.Completed, "No events found to archive", cancellationToken);
                return result;
            }

            result.EventCount = eventCount;

            if (tamperDetectionService is null)
            {
                logger.LogWarning(
                    "Tamper detection is not configured — archiving {Count} events without integrity verification",
                    eventCount);
            }
            else
            {
                logger.LogInformation(
                    "Streaming {Count} events for archive {ArchiveId} with integrity verification",
                    eventCount, result.ArchiveId);
            }

            var containerClient = blobServiceClient.GetBlobContainerClient(_containerName);
            await containerClient.CreateIfNotExistsAsync(cancellationToken: cancellationToken);
            blobClient = containerClient.GetBlobClient(archiveRecord.BlobName);

            using var fileHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var eventsHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

            var eventIds = new List<Guid>(eventCount);
            DateTimeOffset? minDate = null;
            DateTimeOffset? maxDate = null;

            // Pipe connects the producing compressor to the blob uploader. Back-pressure
            // keeps the in-flight buffer small regardless of total archive size.
            var pipe = new Pipe(new PipeOptions(
                pauseWriterThreshold: PipePauseBytes,
                resumeWriterThreshold: PipePauseBytes / 2));

            blobWriteStarted = true;
            long uploadSize = 0;

            // Start upload consumer first so the producer has somewhere to drain into.
            await using var readerStream = pipe.Reader.AsStream();
            var uploadTask = blobClient.UploadAsync(readerStream, overwrite: true, cancellationToken);

            try
            {
                await using (var writerStream = pipe.Writer.AsStream())
                {
                    var countingStream = new CountingHashingStream(writerStream, fileHasher);
                    await using (var gzip = new GZipStream(countingStream, CompressionLevel.Optimal, leaveOpen: true))
                    await using (var jsonWriter = new Utf8JsonWriter(gzip, new JsonWriterOptions { Indented = false }))
                    {
                        jsonWriter.WriteStartObject();

                        jsonWriter.WritePropertyName("events");
                        jsonWriter.WriteStartArray();

                        var eventsWritten = 0;
                        var isFirstForHash = true;

                        await foreach (var entity in auditEventRepository.StreamByDateAsync(
                                               e => e.InsertedDate < archiveBefore, cancellationToken)
                                           .ConfigureAwait(false))
                        {
                            if (entity.EventId == Guid.Empty)
                            {
                                throw new InvalidOperationException(
                                    "Cannot archive - event with empty EventId found");
                            }

                            if (tamperDetectionService is not null)
                            {
                                var isValid = await tamperDetectionService
                                    .VerifyIntegrityAsync(entity.EventId, cancellationToken)
                                    .ConfigureAwait(false);

                                if (!isValid)
                                {
                                    throw new InvalidOperationException(
                                        $"Cannot archive - event {entity.EventId} integrity check failed");
                                }
                            }

                            eventIds.Add(entity.EventId);
                            if (entity.InsertedDate is { } inserted)
                            {
                                if (minDate is null || inserted < minDate) minDate = inserted;
                                if (maxDate is null || inserted > maxDate) maxDate = inserted;
                            }

                            var repr = $"{entity.EventId}:{entity.EventType}:{entity.InsertedDate}";
                            if (!isFirstForHash) eventsHasher.AppendData(EventsHashSeparator);
                            eventsHasher.AppendData(Encoding.UTF8.GetBytes(repr));
                            isFirstForHash = false;

                            var dto = mapper.Map<AuditEventDto>(entity);
                            JsonSerializer.Serialize(jsonWriter, dto, StreamJsonOptions);

                            eventsWritten++;
                            if (eventsWritten % JsonFlushEveryNEvents == 0)
                            {
                                await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }

                        jsonWriter.WriteEndArray();

                        jsonWriter.WritePropertyName("integrity_records");
                        jsonWriter.WriteStartArray();

                        var integrityWritten = 0;
                        await foreach (var integrity in auditIntegrityRepository
                                           .StreamByEventIdsAsync(eventIds, cancellationToken)
                                           .ConfigureAwait(false))
                        {
                            var integrityDto = mapper.Map<AuditIntegrityDto>(integrity);
                            JsonSerializer.Serialize(jsonWriter, integrityDto, StreamJsonOptions);

                            integrityWritten++;
                            if (integrityWritten % JsonFlushEveryNEvents == 0)
                            {
                                await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                            }
                        }

                        jsonWriter.WriteEndArray();

                        var eventsArchiveHash = Convert.ToBase64String(eventsHasher.GetHashAndReset());
                        var createdAt = DateTimeOffset.UtcNow;
                        var effectiveMin = minDate ?? DateTimeOffset.MinValue;
                        var effectiveMax = maxDate ?? DateTimeOffset.MaxValue;

                        jsonWriter.WriteString("archive_id", result.ArchiveId);
                        jsonWriter.WriteString("created_at", createdAt);
                        jsonWriter.WriteString("date_range_start", effectiveMin);
                        jsonWriter.WriteString("date_range_end", effectiveMax);
                        jsonWriter.WriteNumber("event_count", eventIds.Count);

                        jsonWriter.WritePropertyName("metadata");
                        jsonWriter.WriteStartObject();
                        jsonWriter.WriteString("archive_id", result.ArchiveId);
                        jsonWriter.WriteString("archive_version", "1.0");
                        jsonWriter.WriteString("compression_type", "gzip");
                        jsonWriter.WriteString("archive_hash", eventsArchiveHash);
                        jsonWriter.WriteString("created_at", createdAt);
                        jsonWriter.WriteNumber("event_count", eventIds.Count);
                        jsonWriter.WriteString("date_range_start", effectiveMin);
                        jsonWriter.WriteString("date_range_end", effectiveMax);
                        jsonWriter.WriteNumber("size_bytes", 0);
                        jsonWriter.WriteString("status", "InProgress");
                        jsonWriter.WriteEndObject();

                        jsonWriter.WriteEndObject();
                        await jsonWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }

                    // gzip + jsonWriter disposed; the gzip footer has flushed through countingStream
                    // into the pipe writer.
                    uploadSize = countingStream.BytesWritten;
                }
                // writerStream disposed → pipe writer completed → reader sees EOF.
            }
            catch
            {
                // Fault the reader so the upload task unblocks promptly instead of
                // hanging on a drained pipe waiting for more bytes.
                await pipe.Reader.CompleteAsync().ConfigureAwait(false);
                throw;
            }

            await uploadTask.ConfigureAwait(false);

            var fileHashBase64 = Convert.ToBase64String(fileHasher.GetHashAndReset());

            archiveRecord.EventCount = eventIds.Count;
            archiveRecord.DateRangeStart = minDate ?? DateTimeOffset.MinValue;
            archiveRecord.DateRangeEnd = maxDate ?? DateTimeOffset.MaxValue;
            archiveRecord.SizeBytes = uploadSize;
            archiveRecord.Hash = fileHashBase64;
            archiveRecord.Status = MillWorksArchiveStatus.Completed;

            await auditEventRepository.ExecuteInTransactionAsync(async () =>
            {
                await archiveRecordRepository.UpdateAsync(archiveRecord, cancellationToken);

                await auditEventRepository.ExecuteDeleteWhereAsync(
                    e => eventIds.Contains(e.EventId), cancellationToken);

                await auditIntegrityRepository.ExecuteDeleteWhereAsync(
                    i => eventIds.Contains(i.EventId), cancellationToken);

                await archiveRecordRepository.SaveChangesAsync(cancellationToken);
            }, cancellationToken);

            result.Success = true;
            result.EndTime = DateTimeOffset.UtcNow;
            result.Message = $"Successfully archived {eventIds.Count} events";
            result.ArchiveSize = uploadSize;

            logger.LogInformation("Archive completed: {ArchiveId} with {Count} events",
                result.ArchiveId, result.EventCount);

            // STIG AU-9(2): emit an audit event for the completed archive. Wrapped in its
            // own try/catch so a failure here doesn't mark the already-committed archive as
            // failed.
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
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive operation failed");

            // If we started a blob write, a partial or fully-committed blob may exist.
            // Delete it so subsequent retries don't see a half-written payload.
            if (blobWriteStarted && blobClient is not null)
            {
                try
                {
                    await blobClient.DeleteIfExistsAsync(cancellationToken: CancellationToken.None);
                }
                catch (Exception cleanupEx)
                {
                    logger.LogWarning(cleanupEx,
                        "Failed to clean up partial blob for failed archive {ArchiveId}",
                        result.ArchiveId);
                }
            }

            if (archiveRecord != null)
            {
                await archiveRecordRepository.UpdateStatusAsync(result.ArchiveId,
                    MillWorksArchiveStatus.Failed, ex.Message, CancellationToken.None);
            }

            result.Success = false;
            result.Message = $"Archive failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Restores archived audit events by their archive ID.
    /// </summary>
    public async Task<AuditRestoreResult> RestoreArchivedEventsAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        var result = new AuditRestoreResult { ArchiveId = archiveId };

        try
        {
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

            using var downloadStream = new MemoryStream();
            await blobClient.DownloadToAsync(downloadStream, cancellationToken);
            var compressedBytes = downloadStream.ToArray();

            var decompressedData = await DecompressDataAsync(compressedBytes);
            var archiveJson = Encoding.UTF8.GetString(decompressedData);

            var archive = JsonSerializer.Deserialize<AuditArchive>(archiveJson);
            if (archive == null)
            {
                result.Message = "Failed to deserialize archive";
                await archiveRecordRepository.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                    "Failed to deserialize archive data", cancellationToken);
                return result;
            }

            var computedHash = ComputeBytesHash(compressedBytes);
            if (computedHash != archiveRecord.Hash)
            {
                logger.LogError("Archive {ArchiveId} integrity check failed", archiveId);
                result.Message = "Archive integrity check failed";
                await archiveRecordRepository.UpdateStatusAsync(archiveId, MillWorksArchiveStatus.Corrupted,
                    "Hash verification failed", cancellationToken);
                return result;
            }

            await auditEventRepository.ExecuteInTransactionAsync(async () =>
            {
                foreach (var evt in archive.Events)
                {
                    evt.AuditIntegrity = null;
                    AuditEventEntity auditEvent = mapper.Map<AuditEventEntity>(evt);
                    await auditEventRepository.AddAsync(auditEvent, cancellationToken);
                }

                foreach (var integrity in archive.IntegrityRecords)
                {
                    integrity.AuditEvent = null;
                    AuditIntegrityEntity integrityEntity = mapper.Map<AuditIntegrityEntity>(integrity);
                    integrityEntity.AuditEvent = null;

                    await auditIntegrityRepository.AddAsync(integrityEntity, cancellationToken);
                }

                await auditEventRepository.SaveChangesAsync(cancellationToken);
                await auditIntegrityRepository.SaveChangesAsync(cancellationToken);
            }, cancellationToken);

            result.Success = true;
            result.RestoredEventCount = archive.Events.Count;
            result.Message = $"Successfully restored {archive.Events.Count} events";

            logger.LogInformation("Restore completed: {ArchiveId} with {Count} events",
                archiveId, archive.Events.Count);

            return result;
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
    public async Task<List<ArchiveMetadata>> GetArchivesAsync(CancellationToken cancellationToken = default)
    {
        var archiveRecords = await archiveRecordRepository.GetAllOrderedAsync(cancellationToken);
        return mapper.Map<List<ArchiveMetadata>>(archiveRecords);
    }

    /// <summary>
    /// Validates the integrity of an archived audit event by its archive ID.
    /// </summary>
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

            using var downloadStream = new MemoryStream();
            await blobClient.DownloadToAsync(downloadStream, cancellationToken);
            var compressedBytes = downloadStream.ToArray();

            var computedHash = ComputeBytesHash(compressedBytes);
            var isValid = computedHash == archiveRecord.Hash;

            if (isValid)
            {
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

    private static string ComputeBytesHash(byte[] data)
    {
        var hashBytes = SHA256.HashData(data);
        return Convert.ToBase64String(hashBytes);
    }

    private static async Task<byte[]> DecompressDataAsync(byte[] compressedData)
    {
        using var input = new MemoryStream(compressedData);
        using var output = new MemoryStream();
        await using (var gzip = new GZipStream(input, CompressionMode.Decompress))
        {
            await gzip.CopyToAsync(output);
        }

        return output.ToArray();
    }
}
