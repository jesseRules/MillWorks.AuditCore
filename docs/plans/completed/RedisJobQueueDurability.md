# Redis Job Queue and Distributed Lock Durability

**Status:** Resolved
**Date:** 2026-06-09 (code review); revised + implemented 2026-06-16
**Scope:** ~~`RedisJobQueue`~~ (removed), `RedisDistributedLockService`

## Problem

The original review found `RedisJobQueue` shipped as library surface (exercised only by
tests, no production caller in-repo) with broken recovery and completion logic: jobs held
by a crashed worker were permanently lost, completed jobs were never removed from the
processing state, and failed jobs were dropped despite the retry/DLQ data model. The
choice was to make it correct or remove it (greenfield policy — no dead surface).

**Decision taken:** the queue was removed. Commit `34fa147` ("Hardening", 2026-06-10)
deleted `RedisJobQueue.cs`, `RedisJob.cs`, and `RedisJobQueueTests.cs`. The orphaned
`JobQueueConfiguration.cs` it left behind was removed on 2026-06-16. Findings #1–#3 below
are therefore resolved by removal and retained only for history.

One finding remains live: the distributed lock service (#4).

## History (resolved)

The following applied to `RedisJobQueue`, which no longer exists. No further action.

- **#1 — Crashed-worker jobs unrecoverable (Critical).** `RecoverStuckJobsAsync` scanned
  for a TTL state Redis had already deleted, so recovery was dead code and every job held
  by a crashed worker was lost after `JobTimeout`.
- **#2 — `CompleteAsync` deleted the wrong hash field (High).** The processing hash was
  keyed by job JSON, but completion deleted by `jobId`, so completed jobs lingered.
- **#3 — Failed jobs dropped (High).** `RetryCount`/`MaxRetries` and the dead-letter key
  were unused on the failure path; the queue was at-most-once despite an at-least-once
  data model.

## Open Finding

### 4. Distributed lock has no renewal or fencing token (Low — efficiency only; see Verification)

`RedisDistributedLockService.cs:128-190` (acquire), `:220-263` (release/Dispose)

Release is correct (token-checked Lua compare-and-delete, `:228-235`). But if the holder's
work outlasts `expiry`, the key lapses, a second holder acquires, and the overlap is only
noticed post-hoc as a `"was already released or expired"` warning at Dispose (`:242-245`).
No fencing token is returned (`IAuditDistributedLockService.AcquireLockAsync` yields a bare
`IDisposable`), so downstream stores cannot reject a stale holder.

The service is live: registered at `MillWorksAuditBuilder.cs:348`, used by two background
services. Neither depends on the lock for **correctness** — both are guarded at the data
layer (industry-standard: treat a TTL lock as an efficiency optimization only; a lapsed
TTL lock can never be made safe for correctness via renewal — see Kleppmann, *How to do
distributed locking*, 2016):

- **`AuditOutboxDrainer`** — the Redis lock is leader-election only. Correctness comes from
  atomic DB row leases (`LeaseOwner`/`LeaseExpiresAt` claim + `RecoverExpiredLeasesAsync`,
  `AuditOutboxDrainer.cs:342-438`). A lapsed lock cannot cause double-processing here.
- **`DeadLetterQueueProcessor.ProcessOnceAsync`** (`:124`) — has no per-row lease, but
  re-emission **is idempotent at the terminal store**, verified below. A lapsed lock can
  cause two processors to redo the same reprocess *work* (wasted effort, a redundant
  `RetryCount` increment), but never a duplicate audit row.

### Verification — DLQ reprocess is idempotent at the resource layer (2026-06-16)

Traced the reprocess chain end to end:

1. `AuditEvent.EventId` is `Guid { get; init; } = Guid.NewGuid()` with
   `[JsonPropertyName("event_id")]` (`AuditEvent.cs:18`) — set once at creation and
   preserved through the DLQ's JSON serialize→store→deserialize round-trip.
2. `RedisAuditDeadLetterQueue.ReprocessEventAsync` re-emits the *same* stored event via
   `auditLogger.LogAsync(evt.OriginalEvent)` (`:232`).
3. `ConvertToEntity` copies `EventId = auditEvent.EventId` (`AuditLogger.cs:511`) — no
   regeneration.
4. `AuditEventEntity.EventId` is `[Key]` (PK; `b.HasKey("EventId")` in the migration), so a
   second insert of the same event throws a PK violation.
5. The violation is swallowed as success: `catch (DbUpdateException ex) when
   (DuplicateKeyDetector.IsDuplicateKey(ex))` → "Treating as success"
   (`AuditLogger.cs:133-139`). In strict/batched modes the event insert and its integrity
   record share one transaction (`:111-116`, `:87-92`), so the colliding insert rolls back
   the integrity record too — no duplicate integrity record either.

This holds under genuine concurrency (one inserter wins, the other catches the PK
violation), so the guarantee does not depend on the lock. The dedup is keyed by the stable
`EventId` PK — **not** the outbox `IdempotencyKey` (that is a separate path used by
`TransactionalOutboxSink`, irrelevant to this direct-write reprocess).

The dedup operates at the **database** layer, across separate `AuditDbContext` instances —
which is exactly the production model: the DLQ reprocess creates a fresh DI scope (hence a
fresh context) per attempt (`InMemoryAuditDeadLetterQueue.cs:139-144`,
`RedisAuditDeadLetterQueue.cs:227-232`). (Within a single *reused* context EF's change
tracker would instead throw an identity conflict before the DB is touched, but production
never reuses a context across reprocess attempts. The regression test confirmed this
distinction.)

### Consequence

Because correctness is already guaranteed at the resource layer for both callers, #4 is an
**efficiency**, not a correctness, gap — severity drops from Medium toward Low. Renewal and
fencing tokens are not warranted; the light path applies.

**Fix (chosen — light path): DONE 2026-06-16.**

1. ✅ Documented on `IAuditDistributedLockService` that the lock is an efficiency
   optimization only: callers must not rely on it for correctness, and work may outlive
   `expiry` (allowing transient overlap). Both current callers already satisfy this.
2. ✅ Added regression tests across the real backends (all green):
   - `DeadLetterReprocessIdempotencySqliteTests` — double-reprocess yields one row, plus a
     focused test pinning the `AuditLogger` cross-context PK dedup (SQLite `"UNIQUE
     constraint"` branch).
   - `RedisDeadLetterReprocessGarnetTests` — real Garnet (Testcontainers): proves `EventId`
     survives the JSON serialize→Garnet→deserialize round-trip and reprocessing the same
     logical event twice yields one row.
   - `DeadLetterReprocessIdempotencySqlServerTests` — real SQL Server (Testcontainers):
     drives the `SqlException` 2627/2601 duplicate-key branch of `DuplicateKeyDetector`.
   - `RedisDistributedLockGarnetTests` — real Garnet: mutual exclusion while held, the
     finding #4 TTL-lapse overlap (second holder acquires), and that the token-checked
     release does not evict that holder.

Heavier options, recorded but **not** chosen: TTL auto-renewal/heartbeat on `RedisLock`
(reduces overlap probability but is not a correctness mechanism); a fencing token from
`AcquireLockAsync` (textbook, but the resource layer already provides the equivalent).

### Operational finding surfaced by the Garnet integration tests + startup validation added

`RedisDistributedLockService` releases the lock with a Lua script (`ScriptEvaluate` /
`EVAL`). **Garnet ships with Lua scripting disabled by default** — against a default Garnet,
`EVAL` returns `ERR This instance has Lua scripting support disabled`, so every release in
`RedisLock.Dispose` throws (caught and logged) and the lock is only freed when its TTL
expires. Acquire/PING/SET/GET are unaffected. Real Redis enables scripting by default, so
this is Garnet-specific.

**Resolved (2026-06-16):** `RedisDistributedLockService` now performs a startup probe
(`ValidateScriptingSupport`) that runs a trivial `EVAL` when the backend is connected. If
scripting is disabled it logs an actionable error (start Garnet with `--lua`) by default, or
throws when scripting is required. The probe is skipped when the multiplexer is not yet
connected, so it does not interfere with unit tests using a mocked multiplexer.

Operators control fail-fast via a new option:
`SecurityOptions.FailFastOnMissingLockScripting` (default false → log only). It is wired into
the lock-service registration in `MillWorksAuditBuilder` (the service is now constructed
explicitly rather than via `ActivatorUtilities`, because the constructor has two `bool`
parameters and positional resolution would bind the flag to the wrong one). Coverage:
`RedisDistributedLockLuaValidationGarnetTests` — against a no-`--lua` Garnet, direct
construction throws on fail-fast / logs otherwise, and the flag flows end-to-end through the
DI registration; plus a positive check in `RedisDistributedLockGarnetTests` (the `--lua`
fixture passes validation).

## Implementation Outline

1. ✅ Added the efficiency-only contract to the `IAuditDistributedLockService` doc comment.
2. ✅ Added the reprocess idempotency regression tests across SQLite, real Garnet, and real
   SQL Server, plus real-Garnet coverage of the lock's finding #4 behavior
   (`tests/MillWorks.AuditCore.Tests/Integration/DeadLetterReprocessIdempotencySqliteTests.cs`,
   `tests/MillWorks.AuditCore.Tests/Integration/Garnet/`,
   `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/DeadLetterReprocessIdempotencySqlServerTests.cs`).
   Garnet/SQL Server tests run under Testcontainers and skip (Inconclusive) without Docker.

## Non-Goals

- Reviving `RedisJobQueue`. It was removed deliberately; the platform's BackgroundJobs
  library is the job system.
- Replacing the DB row-lease coordination used by the outbox drainer — that path is correct
  and does not depend on the lock for correctness.
