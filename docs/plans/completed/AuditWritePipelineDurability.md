# Audit Write Pipeline Durability

**Status:** Original durability findings implemented (2026-06-09); performance follow-up acceptance open (reviewed 2026-10-16)
**Filing:** Retained under `completed/` because the original findings are implemented; the explicitly open follow-ups below are not complete.
**Date:** 2026-06-09 (code review)
**Scope:** `AuditLogger`, `ResilientAuditLogger`, outbox writer/drainer, dead-letter queue, archival services

## Additional correctness follow-up — 2026-10-16

The original fixes remain delivered. The implementation review found separate gaps in
stale-owner outbox transitions, immediate-batch contract documentation, and per-event duplicate
reporting for mixed replay/new batches, plus mismatched explicit-event IDs across sinks.
These are tracked as H1–H3 (including identity prerequisite H3a) in
[Batch Publishing Hardening](../BatchPublishingHardening.md); H4/H5 cover extraction and load
qualification. Mixed-batch persistence is fixed; individual duplicate classification remains
incomplete. Review validation and its limits are maintained in the hardening plan's
[Evidence and boundaries](../BatchPublishingHardening.md#evidence-and-boundaries) section.

The hardening plan's [MillWorks precedents and gates](../BatchPublishingHardening.md#millworks-precedents-and-implementation-gates)
link shared messaging mechanics, durable failure/recovery, commit receipts, identity policies,
and retention ownership. The remaining AuditCore-specific decisions must be resolved before
implementation; those references do not establish AuditCore runtime acceptance.

The historical findings below describe the original bugs. The sequence-query/runtime/release
acceptance checklist at the end of this plan retains ownership of that validation work.

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

## Original Implementation Outline (completed)

1. Fix #1 first: per-event fallback on batch duplicate-key, plus a regression test reproducing the mixed replay batch (lease expiry + new row).
2. Extend `DuplicateKeyDetector` for provider exceptions; cover with tests that run raw-SQL inserts (#2).
3. Rework DLQ reprocessing to use the undecorated logger and propagate failure (#3); test that a permanently failing event hits the retry cap and is not purged.
4. Add `EnvelopeId` idempotency to entity-change writes (#4) — schema change plus unique index (greenfield, schema-only migration).
5. Apply the remaining medium/low fixes (#5–#10); most are one-to-five-line changes.

## Non-Goals

- Redesigning the outbox/drainer protocol was outside the original scope. The later review identified missing stale-owner enforcement, now tracked in follow-up H1.
- Adding new alert channels.

## Envelope replay lookup performance correction — 2026-10-08

The entity-envelope idempotency lookup added for finding #4 reads both property-change rows and
whole-envelope rows whose `PropertyName` is null. The existing unique
`IX_AuditLogs_Envelope_Property` index filters out null property names, so SQL Server cannot use
it to cover the complete lookup. MillWorks startup diagnostics confirmed parallel clustered-index
scans over more than 305,000 `AuditLogs` rows and repeated audit lookup timeouts.

The correction adds `IX_AuditLogs_Envelope_Lookup`, keyed by `EnvelopeId`, including `PropertyName`,
and filtered only by `EnvelopeId IS NOT NULL`, through a new incremental migration. The existing
unique index and replay behavior remain intact. This is a follow-up to the implemented durability
work, not an unfinished item in the original plan.

**Live verification completed 2026-10-09:** Migration
`20261008124628_AddAuditLogEnvelopeLookupIndex` and its history record were applied and
verified in MillWorks SQL Server. An actual plan used `IX_AuditLogs_Envelope_Lookup` as an
index seek. The same-envelope probe returned the same property row before and after:
logical reads fell from **55,571 to 3**, and query execution time from **1,749 ms to 28 ms**.
These are point measurements, not a workload latency guarantee. The unique index was retained.
The retained TRX records **19 tests, zero skipped**; its original command was not retained.
A reconstructed reproduction command and individual results are in the evidence register. See the
[October 9 verification record](../../evidence/audit-pipeline-2026-10/README.md)
for migration SQL, actual plans, measurements, and test results.

## Sequence integrity scan performance correction — 2026-10-08

MillWorks' `AuditIntegrityScanBackgroundService` calls
`TamperDetectionService.DetectTamperingAsync`, which verifies sequence integrity before checking
recent event hashes and chain links. Rider reported one `GetAllSequenceNumbersAsync` command
returning **217,719 sequence numbers** from `audit.AuditIntegrity`, ordered by sequence number.
The repository materialized the complete column, and the service copied it into a second list
solely to check adjacent numbers for gaps or duplicates.

The implemented correction replaces `IAuditIntegrityRepository.GetAllSequenceNumbersAsync` with
`IsSequenceContiguousAsync`. One database query computes row count, distinct sequence count,
minimum, and maximum and returns at most one summary row. A nonempty sequence is valid when
its row count equals its distinct count and `maximum - minimum + 1` equals its row count.
The range comparison uses decimal arithmetic after reading the summary to avoid Int64 overflow.
`TamperDetectionService.VerifySequenceIntegrityAsync` consumes the boolean result and logs a
sequence gap or duplicate when validation fails.

This preserves gap and duplicate detection, empty-sequence validity, and contiguous sequences
whose first number is not 1. The database still examines the sequence data; the correction bounds
data transfer and application buffering rather than limiting validation to a recent window.
It does not add tail-truncation detection or change hash-chain verification, envelope replay
idempotency, outbox processing, or the lookup-index correction documented above. No schema
change or migration is required for this sequence-query correction.

**Delivery and validation:**

**Version timeline:** October 8 — `1.15.4-dev.1` packed/restored; October 11 — rebuilt API
verified with `1.15.4-dev.3` persistence/dependent packages.

- Built AuditCore and packed all five packages as `1.15.4-dev.1` into MillWorks' `local-packages`
  feed. Updated the 19 MillWorks project files containing direct AuditCore package references
  together; the API restore resolved all five packages at the new version.
- **Historical report: 78 focused tests passed** (original commands/results not recovered;
  see the [test evidence register](../../evidence/audit-pipeline-2026-10/README.md#test-reproducibility)): 29 repository tests, 39 tamper-detection service tests, and 10
  SQLite tamper-detection integration tests. Coverage includes gaps, duplicates that could mask
  a missing number, empty and non-1-based sequences, and Int64 boundary values. SQLite tests
  verify one aggregate command over 2,100 records with and without an interior gap.
- A broader tamper-related test filter stalled and was canceled; it is not recorded as passing.
  The aggregate query was exercised on SQLite, not against the running SQL Server database.
- On October 8, `MillWorks.Api` had only been restored. This was superseded on October 11:
  Jesse confirmed the API was rebuilt and running; the recorded process start was 10:57:10
  local time. Its dependency manifest and persistence DLLs matched the updated `1.15.4-dev.3`
  packages. A subsequent 60-second SQL observation had 12 successful probes and no failures.
  This establishes package delivery and that observed steady-state window, but does not
  verify execution of the sequence aggregate. See the
  [October 11 post-rebuild record](../../evidence/audit-pipeline-2026-10/post-rebuild-record.md).

The sequence correction is implemented and delivered; SQL Server acceptance remains open.
It is not an unfinished original durability finding or a new batch-publishing slice. See the related status note in
[Batch Publishing Redesign](../BatchPublishingRedesign.md#sequence-integrity-scan-performance-follow-up--2026-10-08).

## Remaining acceptance and release work — reviewed 2026-10-16

The records above are historical evidence; this review did not run new live SQL probes or
restart the API. Do not repeat the applied migration or rebuild solely to close stale notes.
Complete the following items in order and attach durable evidence to this plan.

| Item | Status | Required outcome and completion evidence |
|------|--------|------------------------------------------|
| Envelope lookup index | Complete for the recorded MillWorks database | October 9 migration history, actual index seek, identical probe result, and before/after reads are linked above. Verify migration application separately for each additional deployment database. |
| API delivery | Complete for the October 11 deployment | Post-rebuild manifest and DLL checks are recorded above. A later deployment must identify its own package versions; a package reference or restore alone is not runtime evidence. |
| Source/release reconciliation | Package publication and MillWorks restore complete; loaded-runtime acceptance open | `1.15.4-dev.6` was built from the clean release commit, passed 125 selected Release tests, published to both local feeds, and restored successfully in MillWorks. Distributed dev.5/dev.6 must not be overwritten. The [dev.6 release record](../../evidence/audit-pipeline-2026-10/release-dev6/README.md) owns source inventory and build/package delivery evidence. Before any pack, confirm the identity is still unused in all target feeds and bump again if necessary. Reconcile the explicit file inventory below and record a reviewed source commit plus clean/dirty state, SDK/build command, package hashes, dependencies, and migration inclusion. For historical dev.3, retain available package hashes/manifests but mark its exact uncommitted source snapshot unproven; do not assign it a retrospective commit. New live acceptance must use a package tied to the reviewed source, with consumer resolution and loaded DLL hashes recorded. Preserve unrelated work and never overwrite distributed dev.1/dev.3/dev.5. |
| SQL Server sequence correctness | Provider and isolated concurrency checks pass; live/full-schema acceptance remains open | Exercise the actual repository/EF query against a disposable SQL Server database. Cover empty, singleton, contiguous non-1-based, interior gap, duplicate masking a gap, and Int64 boundary cases. Duplicate cases may use an isolated test schema without the unique constraint; do not alter application integrity constraints. Record passing results and confirm one aggregate command returning at most one summary row, without materializing the sequence list. |
| Running API sequence query | Open | Capture the running API integrity scan's generated SQL, parameters (redacted as needed), database row count, execution plan, duration, and logical reads. Confirm count/distinct/min/max translation, no old ordered full-column download, and completion within the numeric predeclared query budget below as well as the configured SQL command timeout. A full index scan inside the aggregate is allowed; returning all sequence rows is not. If either budget is exceeded, retain this item as open and create a fix using the captured plan; do not raise limits after measuring to manufacture a pass. |
| Repeated scan observation | Open; follows live query capture | Record three completed scan invocations with timestamps, package identity, duration, and outcomes; no sequence-query timeout or recurrence of the old full-result warning during those invocations. Record real integrity findings separately rather than suppressing them. Capture the effective scan interval (the host default is 24 hours). Three consecutive scheduled observations span at least 48 hours from the first, or approximately 72 hours from an arbitrary start if the next run is a day away. No public/manual trigger is assumed: the host RunOnceAsync method is internal. Use scheduled runs or an existing supported invocation path; do not silently change the host schedule. A short SQL health probe does not satisfy this item. |


### Isolated SQL Server verification — 2026-10-16

[Fresh retained evidence](../../evidence/audit-pipeline-2026-10/sequence-sqlserver-2026-10-16/README.md)
records 22 passing SQL Server cases against current source: all listed sequential edge cases
with RCSI ON/OFF, plus 217,719-row contiguous/gap cases. Every repository call issued one
aggregate SELECT. The narrow indexed fixture measured 112.8–119.6 ms for the contiguous
volume case; identical-SQL instrumentation replay measured 597–980 logical reads. This does
not qualify full-schema or live performance. That provider-only run preceded the consistent-view implementation and concurrency evidence below;
live/full-schema acceptance remains open. The disposable fixture database was removed.

The read-only application observation found zero integrity rows and no matching cached query;
plan-cache absence cannot establish non-execution. The on-disk API manifest names dev.3, not
a verified loaded assembly from this source. No deployment, API build/restart, host scan trigger,
or schedule change was performed. Running-API and three-scan acceptance remain open.

### Concurrent sequence validation

**Implemented and verified in the isolated fixture, 2026-10-16.**
[Retained consistency evidence](../../evidence/audit-pipeline-2026-10/sequence-consistency-2026-10-16/README.md)
records the protected repository path, real SQL lock-overlap tests with RCSI OFF/ON, caller
and ambient isolation, fresh-context retries, cancellation, deadline cleanup, and original-
volume concurrent append timings. No application SQL setting was changed. Live/full-schema
qualification and a newly packaged source/runtime identity remain open.


SQL Server acceptance must cover append and sanctioned retention concurrently with the actual
repository query. Record READ_COMMITTED_SNAPSHOT and effective transaction isolation; run
provider tests with RCSI both on and off. The SQL Server implementation now reads count/distinct/min/max
from one consistent view: use a short dedicated read-only SERIALIZABLE transaction for the
sequence summary, independent of the hash/chain scan and any caller's weaker ambient transaction.
This protection is now implemented in source and tested on isolated SQL. Do not infer that
previously distributed packages contain it or enable application-wide snapshot settings as
an incidental performance fix.

Use barriers to overlap a committed append, rolled-back append, contiguous-prefix retention,
and interior removal with validation. The result must match a committed serialized state:
append/prefix retention preserves validity; an interior gap in the observed state is invalid.
A transient lock timeout, deadlock, or cancellation is an incomplete scan, not evidence of
sequence corruption. Retry transient lock/deadlock failures at most twice in a fresh scope
with 100 ms backoff between attempts and a 7-second overall sequence-validation budget
(including waits and cleanup). Work is cancelled at 6.75 seconds to reserve 250 ms for cleanup;
each query attempt still has the 2-second budget.
Cancellation is not retried. Exhaustion
records a failed/incomplete scan and leaves acceptance open. Never turn an invalid committed
summary into success merely by retrying until the gap disappears.

Include reader/writer blocking in the query budget and record append latency during these
runs. If SERIALIZABLE cannot meet the budget, keep acceptance open and revise the isolation
strategy with a tested consistent-view alternative before deployment. Count one aggregate
SELECT for the summary separately from transaction-control commands; no sequence-list transfer.

### Controlled performance follow-up — 2026-10-16

[Paired performance evidence](../../evidence/audit-pipeline-2026-10/sequence-performance-2026-10-16/README.md)
now separates query shape, unique-index metadata, connection pooling, and execution overhead.
The normal unique-index/pooled fixture shows a small absolute protection cost. The current
query beats the grouped-count rewrite for that shape; the broader table-lock hint was not
adopted. No production query, isolation, index, or configuration change was justified. The
local application index exists/enabled and configuration uses pooling, but its empty table
cannot establish populated-deployment performance. Preserve the live query/plan and package
acceptance gates above; earlier nonunique/unpooled timing examples are fixture observations.

### Query budget and source inventory

Before running live acceptance, record database identity, row count, SQL version, effective
command timeout, and the following engineering acceptance limits: **at most 2,000 ms elapsed
per sequence aggregate and at most 10,000 total logical page reads per invocation**, for a
recorded dataset of at most 1,000,000 integrity rows. Sum the per-object logical reads from STATISTICS IO, including worktables, once per
invocation; do not also add nested plan-operator counters and double-count those reads, and capture three invocations without clearing shared caches. These are prospective
acceptance limits, not measured claims or a product SLA. If the row count exceeds that range,
set and justify a new numeric duration/read budget before execution; acceptance stays open
until that workload passes. Full-scan elapsed time also includes other hash/chain work and
must be recorded separately. A 29-second query under a 30-second timeout fails this budget.

**Budget rationale:** The 2-second wall-clock gate reserves substantial headroom below the
previously discussed 30-second timeout and bounds interference from a periodic check. The
10,000-page gate limits aggregate logical I/O to roughly 78 MiB of 8 KiB pages per invocation.
Both limits are initial engineering choices, not a measured capacity model; record hardware,
starting index size, warm/cold-cache observations without cache eviction, and concurrent append
latency before claiming they are suitable for a wider deployment. A different workload requires
a separately justified prospective budget, not a retroactive exception to a failed run.

### Reproducible release boundary

Before packing the next unused prerelease, create a reviewed release manifest enumerating every included source,
project/package version, migration, dependency change, and test change, with its reason. The
manifest must explicitly say whether the pending NUnit migration is included; do not accidentally
include it through a whole-working-tree pack. Record the source commit and build only from an
isolated clean checkout of that commit. Require a clean checkout after restore/build excluding
ignored outputs; retained source modifications invalidate that build's release provenance.
Keep the development checkout and unrelated edits intact. If any of the chosen changes depend
on omitted working-tree files, resolve and review that dependency before building.

For the next release, include the existing envelope index, sequence aggregate, transaction cleanup changes,
and their dependencies/tests as a reviewed set; H1/H2/H3a/H3 remain excluded until implemented
and independently accepted. This plan update adds no such implementation. The consistent-view sequence implementation and tests above must be explicitly included in
the reviewed release manifest under a later unused package identity. Do not imply dev.4 or
previously distributed dev.5 packages contain this later source change. Retain exact build/test/pack commands, SDK and lock/dependency resolution,
source-tree identity, package and DLL hashes, packaged migration inspection, and consumer loaded
DLL checks. Recheck feed identity availability immediately before publication. The dev.4 reservation above is historical; current metadata is dev.5. Inspect target feeds
and choose a fresh identity for this consistent-read implementation rather than overwrite
an already distributed package.


The reconciliation change must explicitly include or account for:

- `src/MillWorks.AuditCore.EntityFramework/Migrations/20261008124628_AddAuditLogEnvelopeLookupIndex.cs`
  and its `.Designer.cs`, plus `AuditDbContext` and its model snapshot.
- `tests/MillWorks.AuditCore.Tests/EntityFramework/AuditEnvelopeLookupIndexTests.cs`.
- `tests/MillWorks.AuditCore.Tests/Repositories/AuditSequenceIntegritySqliteTests.cs`.
- `tests/MillWorks.AuditCore.Tests/Repositories/AuditSequenceIntegritySqlServerTests.cs` and
  `AuditSequenceIntegritySqlServerTests.Concurrency.cs`, plus the service incomplete-scan tests.
- `docs/evidence/audit-pipeline-2026-10/sequence-consistency-2026-10-16/` for current source/assembly
  fingerprints, exact command, passing TRX, and the isolated concurrency qualification boundary.
- `tests/MillWorks.AuditCore.Tests/Repositories/RepositoryTransactionFailureTests.cs`.
- `docs/plans/BatchPublishingHardening.md`, both plans updated here, and `docs/evidence/audit-pipeline-2026-10/`.
- Sequence repository interface/implementation, service caller and affected tests; transaction
  cleanup changes; shared/project version overrides and release notes. Do not claim this list
  accounts for unrelated NUnit or other pending working-tree changes without reviewing them.


**Closeout:** Mark performance follow-up acceptance complete only after both SQL Server
correctness and running-API/repeated-scan evidence pass. Record failures and canceled runs
explicitly; the canceled broad test filter is not passing evidence. After the release change
is finalized, record its commit/package identities here and synchronize the batch-plan status.
The shared-host 30-minute acceptance run, SQL test-host isolation, and resource budgeting
belong to MillWorks' SharedSqlHostIsolation plan; they are not proof of sequence-query
acceptance and do not reopen the original durability findings.


### Dev.6 release preparation

The implementation source is committed in `749e6e9488283ca6c62ebbd4b7a67e7742d6d19c`. Its retained consistency-test source
hashes match. [Release inventory](../../evidence/audit-pipeline-2026-10/release-dev6/source-manifest.json)
explicitly includes the committed NUnit 5 migration and dependencies. The dev.6 record owns
new isolated-build/package evidence; historical dirty-tree tests remain labeled historical.
Populated running-API capture and three host scans remain open.


### Dev.6 delivery completed

[Release verification](../../evidence/audit-pipeline-2026-10/release-dev6/README.md) records
immutable package/feed hashes, 125 passing Release tests, all 25 consumer pin updates, and
successful MillWorks.Api restore with five dev.6 packages. Required older dependency pins
were aligned in their actual owning MillWorks projects without suppressing downgrade checks.
The API has not been compiled/restarted by this release task; populated loaded-runtime capture
and three completed host scans remain open.


### H1 library implementation update — 2026-10-16

The separate [H1 hardening implementation](../BatchPublishingH1Implementation.md) now has
[retained local verification](../../evidence/audit-pipeline-2026-10/h1-ownership-2026-10-16/README.md):
299 passed, zero failed and 19 expected provider-specific skips. Guarded ownership, durable
commit evidence, retained failure/handoff state and authorized replay/skip/cleanup are
implemented in the working tree. H1 has new migrations and is not in the published dev.6
sequence package. Its host rollout gates and H2/H3a/H3 remain open; the sequence runtime
acceptance checklist above is unchanged.

H1 was subsequently packaged as [dev.7 and published to both local feeds](../../evidence/audit-pipeline-2026-10/release-dev7/README.md)
after full Release verification (2,536 passed, zero failed). Consumer updates and host rollout
were not performed; this does not close the sequence runtime acceptance above.

### H2 batch contract follow-up — 2026-10-16

[H2 is locally complete](../../evidence/audit-pipeline-2026-10/h2-acceptance-2026-10-16/README.md):
62 focused tests and 2,550 full Release regressions passed, zero failures. Null validation and
acceptance/failure documentation changed; no package, schema or host deployment occurred.
H3a/H3 and the sequence runtime acceptance above remain open.

### H3a identity work — 2026-10-16

The separate [H3a library implementation](../BatchPublishingH3aImplementation.md) is verified,
including SQL Server tests with RCSI off/on. Host rollout remains open. It adds comparison receipts and queue
version 2 and requires coordinated host producer/authorization/data preparation. It does not
close or repeat the sequence runtime acceptance above. No H3a release occurred.

### H3 outcome reporting — 2026-10-16

[H3 is implemented and verified](../BatchPublishingH3Implementation.md): 2,442 broad and 155
focused tests passed, zero failures, including SQL mixed-batch races. This closes the library's
mixed-batch reporting and result-validation work. It does not close sequence host-scan
acceptance, perform a deployment, or implement H4/H5. H2/H3a/H3 remain unshipped.

The subsequent [H3a/H3 review fixes](../../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md)
are verified: 322 focused and 2,443 broad passes, zero failures. They correct two concurrency
interleavings without schema or deployment changes; sequence host-scan acceptance remains open.
