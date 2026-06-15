# Audit Write Pipeline Durability

**Status:** Implemented (2026-06-09)
**Date:** 2026-06-09 (code review)
**Scope:** `AuditLogger`, `ResilientAuditLogger`, outbox writer/drainer, dead-letter queue, archival services

## Problem

A code review of the write path found several ways audit events can be silently lost or falsely reported as persisted. For this library, silent event loss reported as success is the worst possible failure mode. The findings below are ordered by severity.

## Findings

### 1. Whole-batch silent event loss when a batch contains one duplicate (Critical)

`AuditLogger.cs:216-219`, `Services/ResilientAuditLogger.cs:241-245`, `Sinks/Writers/AuditEventBatchWriter.cs:43-55`

`LogBatchAsync` persists the batch atomically, so one duplicate `EventId` rolls back every event in the batch. The catch then reports the whole batch as success:

```csharp
catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
{
    logger.LogDebug("Duplicate key in batch. Treating as success (idempotent replay).");
    return BatchAuditResult.Duplicate(auditEvents.Count);
}
```

The "idempotent replay" assumption only holds when the *entire* batch is a replay. The outbox drainer creates exactly the mixed case: the drainer writes row A's event, loses its lease before marking row A complete; `RecoverExpiredLeasesAsync` resets A to Pending; the next cycle claims A plus new row B in one batch; the insert hits a duplicate PK on A; the transaction rolls back B's insert; `AuditEventBatchWriter` marks both envelopes `WriteOutcome.Duplicate` and `AuditOutboxDrainer.ApplySuccessOutcome` marks both outbox rows Completed. **Event B is permanently lost and reported as success.**

**Fix:** On duplicate-key in a batch, fall back to per-event writes (or pre-filter existing `EventId`s with a `WHERE NOT EXISTS`/lookup) instead of declaring blanket success. `BatchAuditResult.Duplicate` should only be returned when every event is confirmed present.

### 2. `AuditOutboxWriter` duplicate-key catch can never fire (High)

`Sinks/AuditOutboxWriter.cs:138, 176`

```csharp
var inserted = await consumerCtx.Database.ExecuteSqlRawAsync(sql, parameters, cancellationToken);
...
catch (DbUpdateException ex) when (DuplicateKeyDetector.IsDuplicateKey(ex))
```

`DbUpdateException` is only thrown by the `SaveChanges` pipeline; raw SQL surfaces the provider exception (`SqlException` 2627/2601) directly. Both the chunk-level fallback and the per-row fallback in `WriteIndividuallyAsync` are dead code. Under the concurrent-insert race the class itself documents, the `SqlException` propagates out of `PublishAsync` **inside the consumer's transaction**, failing the business write because of an audit duplicate — contradicting the class doc "Duplicate idempotency keys are handled as success."

**Fix:** Teach `DuplicateKeyDetector` to recognize provider exceptions (e.g., `SqlException` 2627/2601, SQLite 19/2067) in addition to `DbUpdateException`, and catch those here.

### 3. DLQ reprocessing falsely reports success (High)

`Implementations/FileBasedAuditDeadLetterQueue.cs:404-441`, `Implementations/InMemoryAuditDeadLetterQueue.cs:131-145`, `Services/ResilientAuditLogger.cs:143-173`

`ReprocessEventAsync` resolves `IAuditLogger` from a fresh scope, which is the decorated `ResilientAuditLogger`. After exhausting retries, `ResilientAuditLogger.LogAsync` stores the event back to the DLQ and returns normally instead of throwing. So `replaySucceeded = true`, the original DLQ entry is marked `IsProcessed` and eventually purged, and a fresh entry with `RetryCount = 0` replaces it. Consequences:

- The `DeadLetterQueueProcessor` `RetryCount < maxRetries` cap is never reached — permanently failing events loop forever.
- If the file DLQ is at hard capacity, `StoreFailedEventAsync` throws inside `LogAsync`, the event is demoted to a temp-dir emergency file while the durable original is marked processed and purged — net loss of durable DLQ state.
- `ReprocessingResult` reports success for failed replays.

**Fix:** Reprocess via the undecorated `AuditLogger` (as `ResilientAuditLogger` itself does), or make `LogAsync` signal failure to its caller so reprocessing can preserve the original entry and its retry count.

### 4. Entity-change envelopes have no write-time idempotency (Medium)

`Sinks/Writers/AuditEntityBatchWriter.cs:46-89` (contrast `AuditEventBatchWriter.cs:116`, which reuses `EnvelopeId` as the PK)

`AuditLogEntity` rows get auto-generated PKs, so when a drainer crashes after `SaveChangesAsync` but before the outbox row is marked Completed, the at-least-once replay inserts the same property changes again. Duplicate audit-trail rows are indistinguishable from real repeated changes.

**Fix:** Derive a deterministic row key from `EnvelopeId + PropertyName`, or store `EnvelopeId` on `AuditLogEntity` with a unique index and skip-on-conflict.

### 5. `EmergencyFallbackAsync` writes `ErrorMessage` unredacted (Medium)

`Services/ResilientAuditLogger.cs:416-435`

The comment says "apply the same redaction as normal persistence," and `CustomFields`/`Target` are redacted — but `auditEvent.ErrorMessage` is serialized raw. The normal path (`AuditLogger.ConvertToEntity`) routes `ErrorMessage` through `fieldRedactor.RedactValue`. Error messages are a canonical leak vector (SQL errors with key values, connection strings) and this file lands in a world-readable temp dir.

**Fix:** `ErrorMessage = fieldRedactor.RedactValue("ErrorMessage", auditEvent.ErrorMessage)`.

### 6. Archive failure path races the still-running blob upload task (Medium)

`AuditArchivalService.cs:211, 336-344, 422-434`

When the producer side of the pipe throws (e.g., integrity check fails), the catch completes the pipe reader and rethrows without awaiting `uploadTask`. The outer catch then calls `blobClient.DeleteIfExistsAsync` while `UploadAsync` may still be committing the truncated payload, so the delete can land before the commit and a partial archive blob survives with the record marked Failed. The exception from `uploadTask` is also unobserved.

**Fix:** In the failure path, try-await the upload task (with a timeout) before blob cleanup.

### 7. `DeadLetterAuditScope.Dispose()` does sync-over-async I/O (Medium)

`Services/DeadLetterAuditScope.cs:74-81`

`SaveAsync().GetAwaiter().GetResult()` blocks on async DLQ I/O (file or Redis), risking deadlock under a synchronization context — the exact reason `CustomAuditScope.Dispose()` was made a deliberate no-op. It also throws from `Dispose` if the DLQ store throws.

**Fix:** Match `CustomAuditScope`'s warn-and-skip sync `Dispose`; require `DisposeAsync` for the save path.

### 8. File DLQ processed-retention measured from `FailedAt`, not `ProcessedAt` (Medium)

`Implementations/FileBasedAuditDeadLetterQueue.cs:544-547`

```csharp
var cutoff = DateTimeOffset.UtcNow - _processedRetention;
var expiredEntries = _fileIndex.Values.Where(e => e.IsProcessed && e.FailedAt < cutoff)
```

An event that failed more than 24h ago and was reprocessed seconds ago is deleted on the next purge cycle — zero retention for slow-to-recover events, the common case. Files just moved to `Processed/` in the same call are also double-counted in the return value.

**Fix:** Gate expiry on `ProcessedAt`.

### 9. `AuditOutboxDrainer` processed-count arithmetic is wrong (Low)

`Sinks/AuditOutboxDrainer.cs:162`

`var processed = validRows.Count - invalidRows.Count;` — invalid rows are not a subset of valid rows. If invalid ≥ valid the result is ≤ 0, which prevents `consecutiveFailures` from resetting and can spuriously open the circuit breaker. Should be `validRows.Count` (or the count of successful outcomes).

### 10. Smaller correctness items (Low)

- `AuditEventRedactionHelper.RedactEvent` drops `Duration` (`AuditEventRedactionHelper.cs:16-46`): `Duration` has a private setter and is never copied nor recomputed even though `EndDate` is copied. Every DLQ-stored/replayed event loses its duration. Call `CalculateDuration()` on the clone.
- `AuditService.GetAuditEvents`/`SearchAuditEvents` ignore the `CancellationToken` (`AuditService.cs:124-127, 209-213`): root cause is `IRepository.GetByOffsetAsync` having no CT parameter. The expensive `JsonData.Contains` scan is uncancellable.
- `AuditService` week/month grouping uses the server-local offset (`AuditService.cs:418, 464`): `DateTimeOffset jan1 = new DateTime(year, 1, 1)` converts via the machine's local zone. `AuditReportService.cs:481` does this correctly with `TimeSpan.Zero`; match it.
- `AuditLogger.BeginOperationAsync` leaks `_activeOperations` entries when `LogAsync` throws (`AuditLogger.cs:267-282`): the catch rethrows without removing the just-added scope.
- `ArchiveAuditEventsAsync` returns `Success = false` for "No events to archive" (`AuditArchivalService.cs:166-171`), so every quiet cycle logs "completed with issues."
- `ArchiveCreationBackgroundService` catch lacks the `when (!stoppingToken.IsCancellationRequested)` filter its sibling services have (`Core/ArchiveCreationBackgroundService.cs:67-70`), logging a spurious error on graceful shutdown.

## Implementation Outline

1. Fix #1 first: per-event fallback on batch duplicate-key, plus a regression test reproducing the mixed replay batch (lease expiry + new row).
2. Extend `DuplicateKeyDetector` for provider exceptions; cover with tests that run raw-SQL inserts (#2).
3. Rework DLQ reprocessing to use the undecorated logger and propagate failure (#3); test that a permanently failing event hits the retry cap and is not purged.
4. Add `EnvelopeId` idempotency to entity-change writes (#4) — schema change plus unique index (greenfield, schema-only migration).
5. Apply the remaining medium/low fixes (#5–#10); most are one-to-five-line changes.

## Non-Goals

- Redesigning the outbox/drainer protocol (its lease model is sound; the bugs are in the duplicate-handling edges).
- Adding new alert channels.
