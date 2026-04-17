using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
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

    /// <summary>
    /// Blob download buffer for the restore pipeline. 4 MiB matches the upload block size
    /// and is the unit of I/O for streaming decompression.
    /// </summary>
    private const int DownloadBufferBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Initial JSON parse buffer on the restore path. Grows on demand if a single event
    /// serializes larger than this; 64 KiB comfortably covers typical audit events.
    /// </summary>
    private const int RestoreParseBufferBytes = 64 * 1024;

    /// <summary>
    /// Hard cap on the JSON parse buffer. Any single element larger than this aborts the
    /// restore — 32 MiB is already well beyond any realistic single audit event.
    /// </summary>
    private const int RestoreParseBufferMaxBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Number of events / integrity records inserted per DB round-trip during restore.
    /// Clears the EF change tracker between batches so tracked-entity memory is bounded.
    /// </summary>
    private const int RestoreBatchSize = 500;

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

                            // InsertedDate is DateTimeOffset? — format with InvariantCulture +
                            // the round-trip "O" specifier so archive hashes are stable across
                            // server locales and never drift between create and verify.
                            var insertedRepr = entity.InsertedDate?.ToString("O", CultureInfo.InvariantCulture) ?? "";
                            var repr = string.Create(CultureInfo.InvariantCulture,
                                $"{entity.EventId}:{entity.EventType}:{insertedRepr}");
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
    /// Restores archived audit events by their archive ID. Streams the compressed blob
    /// through gzip decompression and a <see cref="Utf8JsonReader"/> state machine so
    /// peak memory is bounded by the parse buffer plus a single restore batch, regardless
    /// of archive size. Hash verification is performed incrementally on the compressed
    /// bytes as they are read; a mismatch rolls back the restore transaction.
    /// </summary>
    public async Task<AuditRestoreResult> RestoreArchivedEventsAsync(
        string archiveId,
        CancellationToken cancellationToken = default)
    {
        var result = new AuditRestoreResult { ArchiveId = archiveId };

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
            result.Message =
                "Blob storage client is not configured. Restore requires UseArchival() with ArchivalProvider.AzureBlob.";
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

        try
        {
            var restoredEvents = 0;

            await auditEventRepository.ExecuteInTransactionAsync(async () =>
            {
                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                await using var blobStream = await blobClient.OpenReadAsync(
                    new BlobOpenReadOptions(allowModifications: false) { BufferSize = DownloadBufferBytes },
                    cancellationToken).ConfigureAwait(false);

                await using var hashingStream = new HashingReadStream(blobStream, hasher, leaveOpen: true);
                await using var gzip = new GZipStream(hashingStream, CompressionMode.Decompress, leaveOpen: true);

                restoredEvents = await StreamRestoreAsync(gzip, cancellationToken).ConfigureAwait(false);

                // Drain any bytes GZipStream didn't consume (normally zero for a well-formed
                // archive), so the hash covers the entire blob payload.
                var drainBuffer = new byte[8 * 1024];
                while (await hashingStream.ReadAsync(drainBuffer.AsMemory(), cancellationToken)
                           .ConfigureAwait(false) > 0)
                {
                }

                var computedHash = Convert.ToBase64String(hasher.GetHashAndReset());
                if (!string.Equals(computedHash, archiveRecord.Hash, StringComparison.Ordinal))
                {
                    throw new ArchiveIntegrityException(computedHash, archiveRecord.Hash);
                }
            }, cancellationToken);

            result.Success = true;
            result.RestoredEventCount = restoredEvents;
            result.Message = $"Successfully restored {restoredEvents} events";

            logger.LogInformation("Restore completed: {ArchiveId} with {Count} events",
                archiveId, restoredEvents);

            return result;
        }
        catch (ArchiveIntegrityException ex)
        {
            logger.LogError(
                "Archive {ArchiveId} integrity check failed. Expected: {Expected}, Actual: {Actual}",
                archiveId, ex.ExpectedHash, ex.ComputedHash);

            try
            {
                await archiveRecordRepository.UpdateStatusAsync(archiveId,
                    MillWorksArchiveStatus.Corrupted, "Hash verification failed", CancellationToken.None);
            }
            catch (Exception statusEx)
            {
                logger.LogWarning(statusEx,
                    "Failed to mark archive {ArchiveId} as corrupted after integrity failure",
                    archiveId);
            }

            result.Message = "Archive integrity check failed";
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
    /// Streams the decompressed archive JSON through a <see cref="Utf8JsonReader"/> state
    /// machine, materializing one event / integrity record at a time and persisting them
    /// in batches via the repository. The EF change tracker is cleared between batches so
    /// tracked-entity memory stays bounded by <see cref="RestoreBatchSize"/>.
    /// </summary>
    private async Task<int> StreamRestoreAsync(Stream jsonStream, CancellationToken cancellationToken)
    {
        var pool = ArrayPool<byte>.Shared;
        var buffer = pool.Rent(RestoreParseBufferBytes);
        var eventBatch = new List<AuditEventEntity>(RestoreBatchSize);
        var integrityBatch = new List<AuditIntegrityEntity>(RestoreBatchSize);
        var pendingEvents = new Queue<AuditEventDto>();
        var pendingIntegrity = new Queue<AuditIntegrityDto>();

        try
        {
            var state = new JsonReaderState();
            var bytesInBuffer = 0;
            var endOfStream = false;
            var ctx = new ParseContext();
            var totalEvents = 0;

            while (true)
            {
                if (!endOfStream && bytesInBuffer < buffer.Length)
                {
                    var read = await jsonStream.ReadAsync(
                        buffer.AsMemory(bytesInBuffer, buffer.Length - bytesInBuffer),
                        cancellationToken).ConfigureAwait(false);

                    if (read == 0) endOfStream = true;
                    else bytesInBuffer += read;
                }

                ParseChunk(
                    buffer.AsSpan(0, bytesInBuffer),
                    isFinalBlock: endOfStream,
                    ref state,
                    ref ctx,
                    pendingEvents,
                    pendingIntegrity,
                    out var consumed);

                // Drain pending elements into batched DB inserts. Doing it out here keeps
                // the synchronous ParseChunk free of I/O and gives us the await point
                // for cancellation.
                while (pendingEvents.Count > 0)
                {
                    var dto = pendingEvents.Dequeue();
                    dto.AuditIntegrity = null;
                    var entity = mapper.Map<AuditEventEntity>(dto);
                    entity.AuditIntegrity = null;
                    eventBatch.Add(entity);
                    totalEvents++;

                    if (eventBatch.Count >= RestoreBatchSize)
                    {
                        await FlushEventBatchAsync(eventBatch, cancellationToken).ConfigureAwait(false);
                    }
                }

                while (pendingIntegrity.Count > 0)
                {
                    var dto = pendingIntegrity.Dequeue();
                    dto.AuditEvent = null;
                    var entity = mapper.Map<AuditIntegrityEntity>(dto);
                    entity.AuditEvent = null;
                    integrityBatch.Add(entity);

                    if (integrityBatch.Count >= RestoreBatchSize)
                    {
                        await FlushIntegrityBatchAsync(integrityBatch, cancellationToken).ConfigureAwait(false);
                    }
                }

                // Compact the buffer: drop consumed bytes, keep the remainder at the head.
                if (consumed > 0)
                {
                    var remaining = bytesInBuffer - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(buffer, consumed, buffer, 0, remaining);
                    }

                    bytesInBuffer = remaining;
                }
                else if (!endOfStream && bytesInBuffer == buffer.Length)
                {
                    // No progress possible on this buffer; the current element is larger
                    // than our parse window. Grow up to the cap.
                    if (buffer.Length >= RestoreParseBufferMaxBytes)
                    {
                        throw new JsonException(
                            $"A single archived element exceeds the {RestoreParseBufferMaxBytes:N0}-byte restore parse buffer cap.");
                    }

                    var newSize = Math.Min(buffer.Length * 2, RestoreParseBufferMaxBytes);
                    var bigger = pool.Rent(newSize);
                    Buffer.BlockCopy(buffer, 0, bigger, 0, bytesInBuffer);
                    pool.Return(buffer);
                    buffer = bigger;
                }

                if (endOfStream && bytesInBuffer == 0)
                {
                    break;
                }

                if (endOfStream && consumed == 0 && bytesInBuffer > 0)
                {
                    throw new JsonException("Incomplete JSON at end of archive stream.");
                }
            }

            if (eventBatch.Count > 0)
            {
                await FlushEventBatchAsync(eventBatch, cancellationToken).ConfigureAwait(false);
            }

            if (integrityBatch.Count > 0)
            {
                await FlushIntegrityBatchAsync(integrityBatch, cancellationToken).ConfigureAwait(false);
            }

            if (!ctx.RootEntered)
            {
                throw new JsonException(
                    "Archive payload is not a JSON object — expected an object at top level.");
            }

            return totalEvents;
        }
        finally
        {
            pool.Return(buffer);
        }
    }

    private async Task FlushEventBatchAsync(List<AuditEventEntity> batch, CancellationToken cancellationToken)
    {
        await auditEventRepository.AddRangeAsync(batch, cancellationToken).ConfigureAwait(false);
        await auditEventRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditEventRepository.ClearChangeTrackerAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private async Task FlushIntegrityBatchAsync(List<AuditIntegrityEntity> batch, CancellationToken cancellationToken)
    {
        await auditIntegrityRepository.AddRangeAsync(batch, cancellationToken).ConfigureAwait(false);
        await auditIntegrityRepository.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditIntegrityRepository.ClearChangeTrackerAsync(cancellationToken).ConfigureAwait(false);
        batch.Clear();
    }

    private enum ArrayMode { None, Events, Integrity }

    /// <summary>
    /// Rolling state carried between <see cref="ParseChunk"/> invocations. Tracks depth
    /// within the outer archive object, the most recent property name, and whether we are
    /// inside the <c>events</c> or <c>integrity_records</c> array.
    /// </summary>
    private struct ParseContext
    {
        public int Depth;
        public string? PendingProperty;
        public ArrayMode Array;

        /// <summary>
        /// True once the parser has observed a <c>StartObject</c> at the root (depth 0→1).
        /// Used to reject top-level JSON that isn't an object (null, array, scalar) — those
        /// are corrupt archives and must fail restore rather than silently restoring zero
        /// events.
        /// </summary>
        public bool RootEntered;
    }

    /// <summary>
    /// Parses as much of the archive JSON as fits in <paramref name="span"/>. Events and
    /// integrity records are enqueued into the pending queues; top-level scalars and
    /// nested metadata objects are skipped. On a partial-element buffer (e.g. a single
    /// event whose bytes span this chunk and the next), the reader state is rewound so
    /// the outer loop can refill and retry.
    /// </summary>
    private static void ParseChunk(
        ReadOnlySpan<byte> span,
        bool isFinalBlock,
        ref JsonReaderState state,
        ref ParseContext ctx,
        Queue<AuditEventDto> pendingEvents,
        Queue<AuditIntegrityDto> pendingIntegrity,
        out int consumed)
    {
        var reader = new Utf8JsonReader(span, isFinalBlock, state);

        while (true)
        {
            var stateBeforeRead = reader.CurrentState;
            var consumedBeforeRead = reader.BytesConsumed;

            if (!reader.Read())
            {
                // Insufficient data in buffer for the next token. Outer loop will refill.
                state = reader.CurrentState;
                consumed = (int)reader.BytesConsumed;
                return;
            }

            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    ctx.PendingProperty = reader.GetString();
                    break;

                case JsonTokenType.StartObject:
                {
                    // Depth semantics: ctx.Depth counts open structures at the current
                    // token. Root object → 1, events array → 2, element object → 3.
                    var objectStart = (int)reader.TokenStartIndex;
                    var enteredDepth = ctx.Depth + 1;

                    if (enteredDepth == 3 && ctx.Array == ArrayMode.Events)
                    {
                        if (!reader.TrySkip())
                        {
                            state = stateBeforeRead;
                            consumed = (int)consumedBeforeRead;
                            return;
                        }

                        var objectBytes = span.Slice(objectStart, (int)reader.BytesConsumed - objectStart);
                        var dto = JsonSerializer.Deserialize<AuditEventDto>(objectBytes);
                        if (dto is not null)
                        {
                            pendingEvents.Enqueue(dto);
                        }
                        // TrySkip consumed Start + matching End — depth net-zero.
                    }
                    else if (enteredDepth == 3 && ctx.Array == ArrayMode.Integrity)
                    {
                        if (!reader.TrySkip())
                        {
                            state = stateBeforeRead;
                            consumed = (int)consumedBeforeRead;
                            return;
                        }

                        var objectBytes = span.Slice(objectStart, (int)reader.BytesConsumed - objectStart);
                        var dto = JsonSerializer.Deserialize<AuditIntegrityDto>(objectBytes);
                        if (dto is not null)
                        {
                            pendingIntegrity.Enqueue(dto);
                        }
                    }
                    else if (enteredDepth == 2 && ctx.Array == ArrayMode.None)
                    {
                        // Nested object at root (e.g. `metadata`) — skip contents entirely.
                        if (!reader.TrySkip())
                        {
                            state = stateBeforeRead;
                            consumed = (int)consumedBeforeRead;
                            return;
                        }
                    }
                    else
                    {
                        if (enteredDepth == 1) ctx.RootEntered = true;
                        ctx.Depth = enteredDepth;
                    }

                    break;
                }

                case JsonTokenType.EndObject:
                    ctx.Depth--;
                    break;

                case JsonTokenType.StartArray:
                {
                    var enteredDepth = ctx.Depth + 1;

                    // Top-level arrays ("events" / "integrity_records") live at depth 2
                    // (inside the root object). Anything else — including an unknown array
                    // at depth 2 — is skipped wholesale.
                    if (enteredDepth == 2)
                    {
                        ctx.Array = ctx.PendingProperty switch
                        {
                            "events" => ArrayMode.Events,
                            "integrity_records" => ArrayMode.Integrity,
                            _ => ArrayMode.None
                        };
                        ctx.PendingProperty = null;

                        if (ctx.Array == ArrayMode.None)
                        {
                            if (!reader.TrySkip())
                            {
                                state = stateBeforeRead;
                                consumed = (int)consumedBeforeRead;
                                return;
                            }
                        }
                        else
                        {
                            ctx.Depth = enteredDepth;
                        }
                    }
                    else
                    {
                        ctx.Depth = enteredDepth;
                    }

                    break;
                }

                case JsonTokenType.EndArray:
                    ctx.Depth--;
                    if (ctx.Depth == 1)
                    {
                        ctx.Array = ArrayMode.None;
                    }

                    break;

                // All other tokens (scalars, nulls) at depth >= 1 are top-level archive
                // metadata fields (archive_id, created_at, …) that restore doesn't act on.
                default:
                    break;
            }
        }
    }

    /// <summary>
    /// Internal sentinel so the restore transaction rolls back when hash verification fails
    /// after the streamed inserts have been issued. Caught and translated to a normal
    /// failure result by <see cref="RestoreArchivedEventsAsync"/>.
    /// </summary>
    private sealed class ArchiveIntegrityException(string computedHash, string expectedHash)
        : Exception("Archive integrity check failed")
    {
        public string ComputedHash { get; } = computedHash;
        public string ExpectedHash { get; } = expectedHash;
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

            string computedHash;
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using var blobStream = await blobClient.OpenReadAsync(
                    new BlobOpenReadOptions(allowModifications: false) { BufferSize = DownloadBufferBytes },
                    cancellationToken).ConfigureAwait(false);
                await using var hashingStream = new HashingReadStream(blobStream, hasher, leaveOpen: true);

                var buffer = new byte[DownloadBufferBytes];
                while (await hashingStream.ReadAsync(buffer.AsMemory(), cancellationToken)
                           .ConfigureAwait(false) > 0)
                {
                }

                computedHash = Convert.ToBase64String(hasher.GetHashAndReset());
            }

            var isValid = string.Equals(computedHash, archiveRecord.Hash, StringComparison.Ordinal);

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

}
