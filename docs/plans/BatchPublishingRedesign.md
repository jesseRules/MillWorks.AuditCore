# Batch Publishing Redesign Plan

**Goal:** Provide deterministic internal per-envelope outcomes and durable, idempotent outbox processing. Immediate mixed-kind batches are not currently one atomic transaction; public result visibility remains optional.

---

## Implementation Status

| Slice | Description | Status | Date |
|-------|-------------|--------|------|
| A | `AuditEnvelope.EnvelopeId` for stable identity | ✅ Complete | 2026-05-19 |
| B | Internal `WriteOutcome` / batch writer abstractions | ✅ Complete | 2026-05-19 |
| C | Explicit-event idempotency + outbox `IdempotencyKey` | ✅ Complete | 2026-05-19 |
| D | Stateful outbox row claims (InFlight, leases) | ✅ Complete | 2026-05-19 |
| E | Extract `IAuditBatchProcessor` | ✅ Complete | 2026-05-19 |
| F | Metrics / observability | ✅ Complete | 2026-05-19 |
| G | Public `BatchPublishResult` API | Deferred; requires a demonstrated consumer need | Reviewed 2026-10-16 |

### Correctness follow-up — 2026-10-16

A–F record delivered infrastructure. The subsequent implementation review identified missing
stale-owner enforcement on outbox transitions, an overstatement of immediate-batch atomicity
in the public documentation, and lost individual duplicate classifications in mixed replay/new
explicit-event batches, plus a mismatch between explicit-event outbox and persistence IDs.
These are tracked as H1–H3 (including identity prerequisite H3a) in
[Batch Publishing Hardening](BatchPublishingHardening.md). H4 tracks repository extraction;
H5 tracks load qualification; optional G remains deferred behind a consumer need.
The cross-plan review on 2026-10-16 adds retained failed-row authority, a separately retryable
DLQ projection, durable uncertain-commit verification, queued-contract validation, and
same-identity/different-content conflict rejection to that follow-up. H1–H3 now implement
these requirements; the historical A–F record alone does not establish them. See the current code/release reconciliation below for the remaining host gates.

The hardening plan's [MillWorks precedents and gates](BatchPublishingHardening.md#millworks-precedents-and-implementation-gates)
link shared messaging mechanics, durable failure/recovery, commit receipts, identity policies,
and retention ownership. AuditCore-specific decisions are recorded in the H1/H3a/H3
implementation contracts; those references alone do not establish host runtime acceptance.

Review test evidence and its limits are maintained in the hardening plan's
[Evidence and boundaries](BatchPublishingHardening.md#evidence-and-boundaries) section.
Historical sequence-fix validation is recorded only in the durability plan and its evidence register.

### Sequence consistent-read follow-up — 2026-10-16

The SQL Server summary now runs in a dedicated SERIALIZABLE transaction with bounded
retries and deadline/cancellation handling. [Isolated concurrency evidence](../evidence/audit-pipeline-2026-10/sequence-consistency-2026-10-16/README.md)
proves append/rollback and retention overlap under RCSI OFF/ON, including the original row
volume. This source change does not reopen A–F or implement H1–H3; package/runtime and
scheduled-scan acceptance remain in the durability plan.

### Sequence integrity scan performance follow-up — 2026-10-08

Sequence-query history, index/deployment evidence, and remaining acceptance belong to
[Audit Write Pipeline Durability](completed/AuditWritePipelineDurability.md#remaining-acceptance-and-release-work--reviewed-2026-10-16).

**Slice B Deliverables (2026-05-19):**
- `WriteOutcome` record with `EnvelopeId` correlation, factory methods for Success/Duplicate/Failed
- `IAuditEntityBatchWriter` and `IAuditEventBatchWriter` internal interfaces
- `AuditEntityBatchWriter` extracts logic from `AuditDbContextEntityWriter`, returns outcomes
- `AuditEventBatchWriter` wraps `IAuditLogger.LogBatchAsync`, maps to outcomes
- `ImmediateSink` refactored: splits by Kind upfront, delegates to batch writers, combines outcomes
- Public `IAuditSink` contract unchanged (still returns `Task`)
- 11 test files updated, new unit tests for writers and outcomes

**Slice C Deliverables (2026-05-19):**
- Migration `20260519100000_OutboxIdempotencyAndLeases` (a single migration covering both Slices C and D): adds `IdempotencyKey` column to `AuditOutbox` as `NOT NULL` with `defaultValue: Guid.Empty` — schema-only, greenfield, no data backfill — plus the unique index `UX_AuditOutbox_IdempotencyKey`
- `AuditOutboxEntity.IdempotencyKey` property with `[Required]` attribute
- `AuditDbContext` configures `UX_AuditOutbox_IdempotencyKey` unique index
- `IAuditOutboxWriter` interface updated: accepts `Guid idempotencyKey`, returns insert count/success
- `AuditOutboxWriter` uses `INSERT...WHERE NOT EXISTS` for graceful duplicate handling, falls back to individual inserts on race conditions
- `TransactionalOutboxSink.ExtractIdempotencyKey()` now prefers `ExplicitEventId` for explicit events, otherwise `EnvelopeId`; entity changes use `EnvelopeId`
- `BatchAuditResult.IsDuplicate` property and `Duplicate()` factory method
- `AuditLogger.LogBatchAsync` now confirms all events are duplicates before returning `BatchAuditResult.Duplicate()`; mixed replay/new batches persist new events but need per-event duplicate reporting (H3)
- `AuditEntityBatchWriter` catches `DbUpdateException` via `DuplicateKeyDetector`, returns `WriteOutcome.Duplicate`
- `AuditEventBatchWriter` maps `IsDuplicate` flag to `WriteOutcome.Duplicate`
- At the Slice C checkpoint, `AuditEventBatchWriter.MapToAuditEvent` set `EventId = EnvelopeId`; implemented H3a corrects this to the caller's required `ExplicitEventId` so logical-event replay remains safe across re-enveloping
- 24 new tests: `IdempotencyTests.cs` (18 unit tests), `IdempotencySqliteTests.cs` (6 integration tests proving constraint enforcement)
- Note: Skipped redundant unique index on `AuditEvents.EventId` since it's already the PK

**Slice D Deliverables (2026-05-19):**
- Slice D ships in the same `20260519100000_OutboxIdempotencyAndLeases` migration: adds `LeaseOwner` NVARCHAR(100) and `LeaseExpiresAt` DATETIMEOFFSET columns, drops `IX_AuditOutbox_Status_NextRetryAt_CreatedAt`, and creates `IX_AuditOutbox_Claimable` on `(Status, NextRetryAt, LeaseExpiresAt, CreatedAt)` — a plain composite index, no INCLUDE columns. The `AuditOutboxStatus` enum (Pending=0, InFlight=1, Completed=2, Failed=3) is a code-only change in `AuditOutboxEntity`; the `Status` column remains an `int`, so the migration contains no enum-renumber or Failed-row update SQL (greenfield — no existing rows to migrate)
- `AuditOutboxEntity.LeaseOwner` and `AuditOutboxEntity.LeaseExpiresAt` properties for row-level lease tracking
- `AuditOutboxStatus` enum updated: Pending=0, InFlight=1, Completed=2, Failed=3
- `SecurityOptions.OutboxDrainerLeaseDuration` (default 60s) and `OutboxDrainerLeaseRecoveryInterval` (default 5 min) with validation
- `AuditOutboxDrainer.ClaimBatchAsync()`: provider-specific claim — SQL Server uses atomic `UPDATE...OUTPUT` for best performance; other providers use portable EF Core approach
- `AuditOutboxDrainer.ApplyRowSuccess/ApplyRowRetry/ApplyRowFailureAsync()`: outcome application clears lease columns appropriately
- `AuditOutboxDrainer.RecoverExpiredLeasesAsync()`: periodic sweep resets InFlight rows with expired leases to Pending and clears the lease columns; `AttemptCount` is deliberately left unchanged so a crashed drainer doesn't burn a retry attempt
- Unique lease owner ID per drainer instance: `{hostname}:{pid}:{guid-suffix}`
- New metric: `audit.outbox.drainer.leases_recovered` counter for observability
- 18 new tests in `LeaseClaimTests.cs`: enum values, lease persistence, status transitions, lease recovery, claim query filtering, and the leases-recovered counter metadata; additional `leases_recovered` metric assertions live in `Telemetry/AuditMetricsTests.cs`

**Slice E Deliverables (2026-05-19):**
- `IAuditBatchProcessor` interface with supporting types: `ClaimedOutboxRow`, `BatchProcessingResult`, `RowOutcome`, `RowStatus` enum
- `AuditBatchProcessor` implementation: groups rows by envelope kind, delegates to `IAuditEntityBatchWriter`/`IAuditEventBatchWriter`, maps `WriteOutcome` to `RowOutcome`
- `AuditOutboxDrainer` delegates batch writes to the processor: claim → deserialize → process → apply outcomes; H1 subsequently moved claim/recovery/state persistence into `OutboxStateStore` (remaining H4 seam work below)
- Processor extraction completed; H1 subsequently extracted persistence into `OutboxStateStore`. H4 abstraction/orchestration-test work is deferred with the decision criteria below; there is no line-count completion target.
- Retry exhaustion logic fixed: `ApplyRetryOutcomeAsync` checks `AttemptCount >= MaxAttempts` before scheduling retry, routes to `ApplyExhaustedOutcomeAsync` + DLQ on exhaustion
- Separated `ApplyExhaustedOutcomeAsync` from `ApplyFailedOutcomeAsync` — non-retryable errors still increment attempt count before failing
- DI registration: `IAuditBatchProcessor` → `AuditBatchProcessor` (scoped)
- `InternalsVisibleTo("DynamicProxyGenAssembly2")` added to Services project for Moq support of internal interfaces
- 14 new unit tests in `AuditBatchProcessorTests.cs`: envelope routing, outcome mapping, combined outcomes, writer exception handling, row ID correlation, unknown envelope kind handling

**Slice F Deliverables (2026-05-19):**
- `AuditMetrics.cs`: centralized metrics under `MillWorks.AuditCore` meter
  - Histograms: `audit.outbox.batch_size` (by envelope_kind), `audit.outbox.drain_duration_ms`, `audit.outbox.row_age_seconds` (by envelope_kind)
  - Counters: `audit.envelopes.published`, `audit.envelopes.failed` (by envelope_kind, error_type), `audit.envelopes.duplicate`, `audit.outbox.retry_attempts` (by envelope_kind, error_type), `audit.outbox.dlq_routed`, `audit.outbox.drainer.leases_recovered`
  - Constants: `AuditMetrics.Names.*`, `AuditMetrics.Tags.*`, `AuditMetrics.ErrorTypes.*`
  - `ClassifyError()` method: provider-agnostic error classification (deadlock, timeout, constraint, serialization, unknown) with SQL Server error number detection
- `ClaimedOutboxRow.CreatedAt` property added for row age calculation
- `WriteOutcome.Exception` property added for error classification
- `AuditBatchProcessor` instrumentation: row age histogram, batch size by kind, outcome counters with error classification tags
- `AuditOutboxDrainer` instrumentation: drain duration histogram, migrated to centralized `AuditMetrics` (DLQ routed, leases recovered)
- `TimeProvider` registered as singleton for testable time-dependent logic
- 7 new tests in `AuditBatchProcessorMetricsTests.cs`: row age histogram, batch size by kind, published/duplicate/retry/failed counters with MeterListener verification
- 19 new tests in `AuditMetricsTests.cs`: metric name constants, tag constants, error classification, instrument creation validation

---

## Current State Summary — reviewed 2026-10-18

| Aspect | Implemented behavior | Remaining decision/work |
|--------|----------------------|-------------------------|
| Public `PublishBatchAsync` | Returns `Task`; H2 clarifies sink acceptance and partial-success semantics | Slice G deferred; define a consumer need before designing a return-type change |
| Immediate batch atomicity | Entity/event writers execute independently; one subset may persist before another fails | H2 implemented: corrected documentation, null validation and partial-success regressions |
| Explicit-event identity | H3a requires ExplicitEventId for both outbox and persistence; EnvelopeId remains correlation | Dev.9 EF startup/focused host acceptance passed; other producer/authorization/data gates remain open |
| Internal processing | H3 preserves exact event outcomes; validates logger/writer/processor results and metrics; H1 guards row transitions | Library verified; custom capability implementations require exact metadata; host rollout remains open |
| Outbox state | `Pending=0, InFlight=1, Completed=2, Failed=3`; row claims and expiring leases | H1 guarded ownership updates are implemented locally; migrate and replace all old drainers before relying on them |
| Idempotency | Stable envelope identity, explicit-event PK, outbox unique key, entity envelope/property replay checks | Envelope lookup index verified October 9; see durability plan |
| Observability | Outcome, retry, lease, row-age, and queue-depth instruments implemented | Load targets below remain unverified |

The phase designs below retain the original proposal for reference. Their sample code is not
the implemented API contract. The implementation status above and the dispositions below
govern remaining work; proposed signatures and historical estimates are not release blockers.

### Disposition of remaining design items

| Item | Disposition and trigger | Required outcome if resumed |
|------|-------------------------|----------------------------|
| Slice G: public results | Deferred. Resume only with a named consumer use case that existing exceptions, internal outcomes, and metrics cannot satisfy. | Document acceptance-versus-final-persistence semantics for immediate and outbox sinks, duplicate handling, result correlation, cancellation, and compatibility/versioning before implementation. Then test every envelope outcome, mixed replay/failure, consumer implementations, and migration examples. Outbox acceptance must not claim final audit-store persistence. |
| Outbox repository extraction | Partly delivered by H1 through `OutboxStateStore`; remaining H4 abstraction/tests are independently deferrable. | Retain the existing state store, decide an internal orchestration seam and queue-depth ownership, and preserve H1 guarded ownership predicates and state transitions with SQL Server concurrent-claim, expired-lease, stale-owner, retry-exhaustion, and cancellation tests. Drainer line count is not an acceptance criterion. |
| Load qualification | Unverified; original 10K/sec and P99 <100ms numbers are provisional targets, not demonstrated guarantees. | Define hardware/SQL version, row count, envelope mix, concurrency, polling/lease settings, payload size, and a 30-minute workload before running. Report offered/committed rate, backlog, end-to-end P99, errors, and memory trend; reconcile every envelope as persisted, pending/retry, or DLQ with no silent loss. Set a latency/throughput/memory budget for that workload before claiming a pass. |

---

## Phase 1: Structured Batch Results (Foundation)

**Objective:** Make `PublishBatchAsync` return per-envelope outcomes so callers never guess what succeeded.

### 1.1 Add Stable Envelope Identity

**File:** `MillWorks.AuditCore.Abstractions/Models/AuditEnvelope.cs`

```csharp
public sealed record AuditEnvelope
{
    // Stable identity for result correlation. This is NOT the same thing as an
    // explicit event's AuditEvent.EventId and must exist for EntityChange envelopes too.
    public Guid EnvelopeId { get; init; } = Guid.NewGuid();
}
```

- Producers must preserve `EnvelopeId` across retries and outbox serialization.
- `EnvelopeId` is the correlation key for `BatchPublishResult`.
- `EventId` remains the idempotency key for explicit-event persistence; do not overload it
  to mean "batch item identity."

### 1.2 Define Result Types

**File:** `MillWorks.AuditCore.Abstractions/Results/BatchPublishResult.cs`

```csharp
public sealed class BatchPublishResult
{
    public IReadOnlyList<EnvelopeOutcome> Outcomes { get; init; }
    public bool AllSucceeded => Outcomes.All(o =>
        o.Status is EnvelopeStatus.Succeeded or EnvelopeStatus.Duplicate);
    public int SucceededCount => Outcomes.Count(o => o.Status == EnvelopeStatus.Succeeded);
    public int FailedCount => Outcomes.Count(o => o.Status == EnvelopeStatus.Failed);
    public int DuplicateCount => Outcomes.Count(o => o.Status == EnvelopeStatus.Duplicate);
}

public sealed class EnvelopeOutcome
{
    public required Guid EnvelopeId { get; init; }
    public required EnvelopeStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }
    public bool IsRetryable { get; init; }
}

public enum EnvelopeStatus
{
    Succeeded,
    Failed,
    Duplicate  // idempotent replay detected
}
```

### 1.3 Update IAuditSink Interface

**File:** `MillWorks.AuditCore.Abstractions/Interfaces/IAuditSink.cs`

```csharp
// Change signature
Task<BatchPublishResult> PublishBatchAsync(
    IReadOnlyList<AuditEnvelope> envelopes,
    CancellationToken cancellationToken = default);
```

### 1.4 Update ImmediateSink

**File:** `MillWorks.AuditCore.Services/Sinks/ImmediateSink.cs`

- Collect outcomes for each envelope during processing
- Return `BatchPublishResult` with per-envelope status
- Mark duplicates as `EnvelopeStatus.Duplicate` (success, not failure)

### 1.5 Update TransactionalOutboxSink

**File:** `MillWorks.AuditCore.Services/Sinks/TransactionalOutboxSink.cs`

- Return success outcomes for all envelopes written to outbox
- Outcomes describe acceptance inside the consumer transaction, which may later roll back; they do not establish final audit-store persistence. Define failure and cancellation behavior as part of G.

### 1.6 Update AuditOutboxDrainer

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

- Use `BatchPublishResult.Outcomes` to map success/failure back to outbox rows
- Preserve `(OutboxRowId, EnvelopeId)` correlation inside the drainer/processor layer;
  sinks must not know outbox row ids
- Eliminate blind fallback to one-at-a-time (only retry actual failures)
- Match outcomes back to rows by `EnvelopeId`

**Acceptance Criteria:**
- [x] `AuditEnvelope` has a stable `EnvelopeId` *(Slice A - completed 2026-05-19)*
- [ ] `PublishBatchAsync` returns `BatchPublishResult` *(Slice G - deferred)*
- [x] Drainer uses outcomes to update only failed rows *(Slice E - completed; `AuditBatchProcessor` + drainer `ApplyOutcomesAsync` route each row by `RowStatus`, no blind one-at-a-time fallback)*
- [x] Duplicate detection returns `Duplicate` status, not exception *(Slice C - completed; `WriteOutcome.Duplicate` → `RowStatus.Duplicate` → drainer completes the row)*
- [x] All existing tests updated and passing

---

## Phase 2: Normalize Persistence by Envelope Kind

**Objective:** Route all envelope kinds through internal writers with identical semantics—no side effects during batch enumeration.

### 2.1 Define Internal Writer Interfaces

**File:** `MillWorks.AuditCore.Services/Sinks/Writers/IAuditEntityBatchWriter.cs`

```csharp
public interface IAuditEntityBatchWriter
{
    Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken);
}

public sealed class WriteOutcome
{
    public required Guid EnvelopeId { get; init; }
    public required bool Succeeded { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsDuplicate { get; init; }
    public bool IsRetryable { get; init; }
}
```

**File:** `MillWorks.AuditCore.Services/Sinks/Writers/IAuditEventBatchWriter.cs`

```csharp
public interface IAuditEventBatchWriter
{
    Task<IReadOnlyList<WriteOutcome>> WriteBatchAsync(
        IReadOnlyList<AuditEnvelope> envelopes,
        CancellationToken cancellationToken);
}
```

### 2.2 Implement AuditEntityBatchWriter

**File:** `MillWorks.AuditCore.Services/Sinks/Writers/AuditEntityBatchWriter.cs`

- Extract entity-change writing logic from current `AuditDbContextEntityWriter`
- Return per-envelope outcomes
- Handle duplicates via constraint detection

### 2.3 Implement AuditEventBatchWriter

**File:** `MillWorks.AuditCore.Services/Sinks/Writers/AuditEventBatchWriter.cs`

- Batch explicit events into a single internal operation with the same contract as
  entity changes: either atomic commit of the whole explicit-event subset, or a
  `WriteOutcome` per envelope with no inline side effects during enumeration
- Prefer a direct repository/bulk-write path over reusing `IAuditLogger.LogBatchAsync`
  if the logger contract cannot expose deterministic per-envelope outcomes
- Return per-envelope outcomes

### 2.4 Refactor ImmediateSink as Coordinator

**File:** `MillWorks.AuditCore.Services/Sinks/ImmediateSink.cs`

```csharp
public async Task<BatchPublishResult> PublishBatchAsync(
    IReadOnlyList<AuditEnvelope> envelopes,
    CancellationToken cancellationToken)
{
    var entityChanges = envelopes.Where(e => e.Kind == AuditEnvelopeKind.EntityChange).ToList();
    var explicitEvents = envelopes.Where(e => e.Kind == AuditEnvelopeKind.ExplicitEvent).ToList();

    var entityOutcomes = await _entityBatchWriter.WriteBatchAsync(entityChanges, cancellationToken);
    var eventOutcomes = await _eventBatchWriter.WriteBatchAsync(explicitEvents, cancellationToken);

    return BuildResult(entityOutcomes, eventOutcomes);
}
```

**Acceptance Criteria:**
- [x] No inline side effects during batch iteration *(Slice B - completed 2026-05-19)*
- [x] Both entity changes and explicit events batch-written *(Slice B - completed 2026-05-19)*
- [x] Coordinator combines outcomes from both writers *(Slice B - completed 2026-05-19)*
- Not applicable: the implemented drainer consumes internal row outcomes. Public result changes belong only to deferred Slice G.

---

## Phase 3: First-Class Idempotency for Explicit Events

**Objective:** Every persisted explicit event has a stable idempotency key; DB enforces uniqueness; retries are safe.

### 3.1 Add Idempotency Key to AuditEvent

**File:** `MillWorks.AuditCore.Abstractions/Models/AuditEvent.cs`

```csharp
// EventId already exists and serves as the natural idempotency key
public Guid EventId { get; init; }  // Already present

// Ensure EventId is always populated (required, not optional)
```

### 3.2 Add Unique Constraint to Audit Event Table

**File:** New migration in `MillWorks.AuditCore.EntityFramework/Migrations/`

```sql
CREATE UNIQUE INDEX UX_AuditEvents_EventId 
ON [audit].[AuditEvents] (EventId)
WHERE EventId IS NOT NULL;
```

### 3.3 Add IdempotencyKey to Outbox Entity

**File:** `MillWorks.AuditCore.EntityFramework/Entities/AuditOutboxEntity.cs`

```csharp
// Derived from envelope identity. For ExplicitEvent: AuditEvent.EventId.
// For EntityChange: AuditEnvelope.EnvelopeId.
public Guid IdempotencyKey { get; set; }
```

**Migration:**
```sql
ALTER TABLE [audit].[AuditOutbox] 
ADD IdempotencyKey UNIQUEIDENTIFIER NOT NULL;

CREATE UNIQUE INDEX UX_AuditOutbox_IdempotencyKey 
ON [audit].[AuditOutbox] (IdempotencyKey);
```

### 3.4 Update AuditOutboxWriter

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxWriter.cs`

- Extract `IdempotencyKey` from envelope before writing
- Handle duplicate key constraint as success (row already queued)
- Preserve `EnvelopeId` during serialization so a replayed outbox row maps back to the
  same logical envelope

### 3.5 Update Drainer Duplicate Detection

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

- When sink returns `Duplicate` status, mark row `Completed` (not retry)
- Log at DEBUG level for observability

**Acceptance Criteria:**
- [x] Unique constraint on `AuditEvents.EventId` — Skipped (already PK, inherently unique)
- [x] Unique constraint on `AuditOutbox.IdempotencyKey`
- [x] Duplicate writes return success, not throw
- [x] Drainer replay is safe (no duplicate audit records)
- [x] Metrics track duplicate counts — `audit.envelopes.duplicate` counter delivered in Slice F

---

## Phase 4: Stateful Outbox Processing with Row-Level Claims

**Objective:** Replace "try batch, then guess" with explicit row states and lease ownership.

### 4.1 Extend Outbox Status Enum

**File:** `MillWorks.AuditCore.EntityFramework/Entities/AuditOutboxEntity.cs`

```csharp
public enum OutboxStatus
{
    Pending = 0,
    InFlight = 1,   // NEW: claimed by drainer
    Completed = 2,
    Failed = 3      // Renamed from 2 to 3
}
```

### 4.2 Add Lease Columns

**File:** `MillWorks.AuditCore.EntityFramework/Entities/AuditOutboxEntity.cs`

```csharp
public string? LeaseOwner { get; set; }          // Drainer instance ID
public DateTimeOffset? LeaseExpiresAt { get; set; }  // Lease TTL
```

**Migration:**
```sql
ALTER TABLE [audit].[AuditOutbox] ADD LeaseOwner NVARCHAR(100) NULL;
ALTER TABLE [audit].[AuditOutbox] ADD LeaseExpiresAt DATETIMEOFFSET NULL;

-- Update existing Failed rows (Status=2 -> Status=3)
UPDATE [audit].[AuditOutbox] SET Status = 3 WHERE Status = 2;
```

### 4.3 Implement Lease Acquisition

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

```csharp
private async Task<List<AuditOutboxEntity>> ClaimBatchAsync(CancellationToken ct)
{
    var leaseId = GenerateLeaseId();
    var leaseExpiry = _timeProvider.GetUtcNow().Add(_options.LeaseDuration);
    
    // Atomic claim: UPDATE ... SET Status=InFlight, LeaseOwner=@id, LeaseExpiresAt=@expiry
    // WHERE Status=Pending AND (NextRetryAt IS NULL OR NextRetryAt <= @now)
    // AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt < @now)
    // LIMIT @batchSize
    
    return await _outboxRepository.ClaimBatchAsync(leaseId, leaseExpiry, batchSize, ct);
}
```

### 4.4 Implement Lease Release

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

```csharp
// On success: Status = Completed, clear lease
// On failure: Status = Pending (retry) or Failed (exhausted), clear lease
// On crash: expired lease allows another drainer to reclaim
```

### 4.5 Add Lease Expiry Recovery

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

```csharp
// Periodic sweep: find InFlight rows with expired leases
// Reset to Pending, clear lease columns
```

> **As built:** recovery leaves `AttemptCount` unchanged (not incremented). A lease
> expiring means the drainer crashed, not that the envelope failed to process, so
> burning a retry attempt on a crash would be incorrect. Genuine processing
> failures still increment `AttemptCount` via the normal retry path.

### 4.6 Update Drainer Query Index

**Migration:**
```sql
DROP INDEX IX_AuditOutbox_Status_NextRetryAt_CreatedAt;

CREATE INDEX IX_AuditOutbox_Claimable ON [audit].[AuditOutbox] 
(Status, NextRetryAt, LeaseExpiresAt, CreatedAt)
INCLUDE (EnvelopeJson, EnvelopeVersion, IdempotencyKey);
```

**Acceptance Criteria:**
- [x] Rows transition: Pending → InFlight → Completed/Failed
- [x] Lease columns populated during claim
- [x] Expired leases recovered automatically
- [ ] Overlapping workers cannot apply stale outcomes after lease expiry/reclaim (H1); repeated processing must remain idempotent
- [x] Metrics track InFlight count and lease recovery rate — lease recovery counter `audit.outbox.drainer.leases_recovered` plus the `audit.outbox.inflight_count` gauge (`AuditOutboxQueueObserver`, 2026-06-16)

---

## Phase 5: Separate Orchestration from Persistence

**Objective:** Drainer hands claimed batches to a processor component; drainer doesn't know sink internals.

### 5.1 Define Batch Processor Interface

**File:** `MillWorks.AuditCore.Services/Sinks/Processing/IAuditBatchProcessor.cs`

```csharp
public interface IAuditBatchProcessor
{
    Task<BatchProcessingResult> ProcessBatchAsync(
        IReadOnlyList<ClaimedOutboxRow> rows,
        CancellationToken cancellationToken);
}

public sealed class ClaimedOutboxRow
{
    public required Guid RowId { get; init; }
    public required AuditEnvelope Envelope { get; init; }
    public required int AttemptCount { get; init; }
}

public sealed class BatchProcessingResult
{
    public IReadOnlyList<RowOutcome> Outcomes { get; init; }
}

public sealed class RowOutcome
{
    public required Guid RowId { get; init; }
    public required RowStatus Status { get; init; }
    public string? ErrorMessage { get; init; }
    public bool IsRetryable { get; init; }
    public TimeSpan? RetryAfter { get; init; }  // Processor-suggested backoff
}

public enum RowStatus
{
    Succeeded,
    Failed,
    Duplicate,
    RetryLater
}
```

### 5.2 Implement AuditBatchProcessor

**File:** `MillWorks.AuditCore.Services/Sinks/Processing/AuditBatchProcessor.cs`

- Groups rows by envelope kind
- Delegates to `IAuditEntityBatchWriter` and `IAuditEventBatchWriter`
- Maps write outcomes to row outcomes
- Handles retryable vs non-retryable classification

### 5.3 Simplify AuditOutboxDrainer

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    while (!stoppingToken.IsCancellationRequested)
    {
        var claimedRows = await ClaimBatchAsync(stoppingToken);
        if (claimedRows.Count == 0)
        {
            await Task.Delay(_pollInterval, stoppingToken);
            continue;
        }

        var result = await _batchProcessor.ProcessBatchAsync(claimedRows, stoppingToken);
        await ApplyOutcomesAsync(result.Outcomes, stoppingToken);
    }
}

private async Task ApplyOutcomesAsync(IReadOnlyList<RowOutcome> outcomes, CancellationToken ct)
{
    foreach (var outcome in outcomes)
    {
        switch (outcome.Status)
        {
            case RowStatus.Succeeded:
            case RowStatus.Duplicate:
                await _outboxRepository.CompleteRowAsync(outcome.RowId, ct);
                break;
            case RowStatus.RetryLater:
                await _outboxRepository.ScheduleRetryAsync(outcome.RowId, outcome.RetryAfter, ct);
                break;
            case RowStatus.Failed:
                await _outboxRepository.FailRowAsync(outcome.RowId, outcome.ErrorMessage, ct);
                break;
        }
    }
}
```

### 5.4 Extract Outbox Repository (deferred design)

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxRepository.cs`

- `ClaimBatchAsync(leaseId, expiry, batchSize)`
- `CompleteRowAsync(claim)`
- `ScheduleRetryAsync(claim, retryAfter)`
- `FailRowAsync(claim, errorMessage)`

These are conceptual signatures. H1/H4 require a fresh acquisition token in `claim` and
conditional mutations; a row ID alone is insufficient to authorize a transition.
Recovery (`RecoverExpiredLeasesAsync`) must also apply conditional state/expiry predicates.

**Acceptance Criteria:**
- [x] Processor routes envelopes to internal writers and maps per-row outcomes (Slice E).
- [x] Processor unit tests use mocked writers without a DbContext (`AuditBatchProcessorTests`).
- H1 already extracted persistence into `Recovery/OutboxStateStore*.cs`; H4 retains the internal abstraction decision and isolated orchestration tests, independently deferrable.
- Removed: the ~100-line drainer target; correctness and ownership semantics determine acceptance.

---

## Phase 6: Production-Grade Observability

**Objective:** Metrics and traces that make the library operable, not just functional.

### 6.1 Define Metrics

**File:** `MillWorks.AuditCore.Services/Telemetry/AuditMetrics.cs`

```csharp
public static class AuditMetrics
{
    private static readonly Meter Meter = new("MillWorks.AuditCore", "1.0.0");

    // Histograms
    public static readonly Histogram<int> OutboxBatchSize = 
        Meter.CreateHistogram<int>("audit.outbox.batch_size");
    
    public static readonly Histogram<double> OutboxDrainDuration = 
        Meter.CreateHistogram<double>("audit.outbox.drain_duration_ms");
    
    public static readonly Histogram<double> OutboxRowAge = 
        Meter.CreateHistogram<double>("audit.outbox.row_age_seconds");

    // Counters
    public static readonly Counter<long> EnvelopesPublished = 
        Meter.CreateCounter<long>("audit.envelopes.published");
    
    public static readonly Counter<long> EnvelopesFailed = 
        Meter.CreateCounter<long>("audit.envelopes.failed");
    
    public static readonly Counter<long> EnvelopesDuplicate = 
        Meter.CreateCounter<long>("audit.envelopes.duplicate");
    
    public static readonly Counter<long> RetryAttempts = 
        Meter.CreateCounter<long>("audit.outbox.retry_attempts");
    
    public static readonly Counter<long> DlqRouted = 
        Meter.CreateCounter<long>("audit.outbox.dlq_routed");
    
    public static readonly Counter<long> LeasesRecovered = 
        Meter.CreateCounter<long>("audit.outbox.leases_recovered");

    // Gauges (via ObservableGauge)
    // - audit.outbox.pending_count
    // - audit.outbox.inflight_count
    // - audit.outbox.oldest_pending_age_seconds
}
```

### 6.2 Instrument Batch Processor

**File:** `MillWorks.AuditCore.Services/Sinks/Processing/AuditBatchProcessor.cs`

```csharp
// Before processing
AuditMetrics.OutboxBatchSize.Record(rows.Count, 
    new("envelope_kind", "entity_change"));

var sw = Stopwatch.StartNew();
// ... process ...
sw.Stop();

AuditMetrics.OutboxDrainDuration.Record(sw.Elapsed.TotalMilliseconds);

// Per-outcome recording
foreach (var outcome in outcomes)
{
    switch (outcome.Status)
    {
        case RowStatus.Succeeded:
            AuditMetrics.EnvelopesPublished.Add(1, envelopeKindTag);
            break;
        case RowStatus.Failed:
            AuditMetrics.EnvelopesFailed.Add(1, envelopeKindTag, errorTypeTag);
            break;
        case RowStatus.Duplicate:
            AuditMetrics.EnvelopesDuplicate.Add(1, envelopeKindTag);
            break;
        case RowStatus.RetryLater:
            AuditMetrics.RetryAttempts.Add(1, envelopeKindTag);
            break;
    }
}
```

### 6.3 Add Queue Depth Observable ✅ *(implemented 2026-06-16)*

**Files:** `MillWorks.AuditCore.Services/Telemetry/AuditOutboxQueueObserver.cs`,
`MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

The naive sketch below blocks the metrics-collection thread inside the gauge callback
(`GetAwaiter().GetResult()` on a non-thread-safe `DbContext`) and was **not** used.

```csharp
// NOT IMPLEMENTED — illustrative of the hazard, do not copy.
AuditMetrics.Meter.CreateObservableGauge(
    "audit.outbox.pending_count",
    () => _outboxRepository.GetPendingCountAsync().GetAwaiter().GetResult());
```

**As built — push/cache model:** `AuditOutboxQueueObserver` (singleton) owns three
`ObservableGauge<long>` instruments and reads only cached fields via `Interlocked`:
`audit.outbox.pending_count`, `audit.outbox.inflight_count`, and
`audit.outbox.oldest_pending_age_seconds`. The drainer samples the outbox on its own
cadence (`SecurityOptions.OutboxQueueDepthSampleInterval`, default 10s) via the internal
`AuditOutboxDrainer.ComputeQueueDepthAsync` (three index-backed aggregate queries) and
pushes the result into the observer with `Update(...)`. Sampling runs outside the leader
lock in its own scope, so every instance keeps its gauges fresh and a sampling failure
never trips the drain circuit breaker. Because each instance reports the global depth
independently, aggregate these gauges with `max`/`mean`, never `sum`.

### 6.4 Add Row Age Histogram

**File:** `MillWorks.AuditCore.Services/Sinks/Processing/AuditBatchProcessor.cs`

```csharp
foreach (var row in rows)
{
    var age = _timeProvider.GetUtcNow() - row.CreatedAt;
    AuditMetrics.OutboxRowAge.Record(age.TotalSeconds);
}
```

### 6.5 Add Error Classification Tags

```csharp
// Tags for EnvelopesFailed counter
new KeyValuePair<string, object?>("error_type", ClassifyError(ex))

private static string ClassifyError(Exception ex) => ex switch
{
    DbUpdateException { InnerException: SqlException { Number: 1205 } } => "deadlock",
    DbUpdateException { InnerException: SqlException { Number: -2 } } => "timeout",
    DbUpdateException => "constraint",
    JsonException => "serialization",
    _ => "unknown"
};
```

**Acceptance Criteria:**
- [x] Batch size histogram by envelope kind — `audit.outbox.batch_size` with `envelope_kind` tag
- [x] Success/failure/duplicate counters by kind — `audit.envelopes.published/failed/duplicate` with `envelope_kind` tag
- [x] Retry count metric — `audit.outbox.retry_attempts` with `envelope_kind` and `error_type` tags
- [x] DLQ routing counter — `audit.outbox.dlq_routed`
- [x] Lease recovery counter — `audit.outbox.drainer.leases_recovered`
- [x] Queue depth gauge (pending + inflight) — `audit.outbox.pending_count` / `audit.outbox.inflight_count` via `AuditOutboxQueueObserver` (push/cache model, no DbContext in callback) *(2026-06-16)*
- [x] Oldest pending row age gauge — `audit.outbox.oldest_pending_age_seconds` via the same observer *(2026-06-16)*
- [x] Row age histogram at drain time — `audit.outbox.row_age_seconds` with `envelope_kind` tag
- [x] Error classification tags — `error_type` tag with deadlock/timeout/constraint/serialization/unknown values

---

## Implementation Order & Dependencies

### Recommended Delivery Strategy

Do **not** implement this in the same order as the conceptual architecture above.
The safest rollout is:

1. Introduce stable envelope identity and internal writer abstractions first.
2. Build deterministic internal processing behind the existing public API.
3. Add explicit-event idempotency and outbox row-level identity.
4. Add stateful outbox claiming / leases.
5. Only then change the public `IAuditSink.PublishBatchAsync` contract if it is
   still necessary.

This keeps the library shippable after each slice and avoids coupling a public
API break to unfinished internal semantics.

```
Phase 1 ─────────────────────────────────────────────────────►
         │
         └── Phase 2 ────────────────────────────────────────►
                      │
                      ├── Phase 3 ───────────────────────────►
                      │
                      └── Phase 4 ───────────────────────────►
                                   │
                                   └── Phase 5 ──────────────►
                                                │
                                                └── Phase 6 ─►
```

### Ticketable Rollout

| Slice | Scope | Depends On | Public Break? | Est. Effort | Status |
|-------|-------|------------|---------------|-------------|--------|
| A | Add `AuditEnvelope.EnvelopeId`; preserve through interceptor, immediate sink, outbox serialization, drainer deserialization | None | Yes: `AuditEnvelope` contract | 1 day | ✅ Done |
| B | Introduce internal `WriteOutcome` / batch writer abstractions for entity changes and explicit events; refactor `ImmediateSink` to coordinate them behind the current API | A | No | 2-3 days | ✅ Done |
| C | Add explicit-event idempotency constraints and outbox `IdempotencyKey`; handle duplicates as success | A | Yes: migration | 1-2 days | ✅ Done |
| D | Add stateful outbox row claims (`InFlight`, lease columns, repository methods, claim/apply transitions) | C | Yes: migration | 2-3 days | ✅ Done |
| E | Extract `IAuditBatchProcessor`; make drainer orchestration-only | B, D | No | 1-2 days | ✅ Done |
| F | Add metrics / observability | E | No | 1 day | ✅ Done |
| G | Change public `IAuditSink.PublishBatchAsync` to return `BatchPublishResult` if internal processing still cannot expose needed visibility without it | B, C, D, E | Yes: public API | 1-2 days | Deferred; consumer need required |

**Realistic total:** ~8-14 engineering days depending on migration/test overhead.

### Recommendation

[H1](BatchPublishingHardening.md#h1--enforce-claim-ownership-at-the-database-boundary) is implemented
and locally verified; dev.7 is on both local feeds and host rollout remains open.
[H2](BatchPublishingHardening.md#h2--correct-public-batch-acceptance-and-failure-documentation)
is implemented, verified and published locally as dev.8. H3a library implementation and SQL verification are complete; host rollout gates remain open.
See the [H3a contract](BatchPublishingH3aImplementation.md). H3 exact internal outcomes are implemented and verified (2,442 broad and 155 focused passes); see its
[contract](BatchPublishingH3Implementation.md).
Keep Slice G deferred until a consumer need is demonstrated. H4/H5 retain their independent scope and acceptance gates in the hardening plan.

---

## Migration Strategy

### Database Migrations

> **As built:** Phases 3 and 4 shipped as a single migration
> `20260519100000_OutboxIdempotencyAndLeases` rather than the two separate
> migrations originally planned below. Because AuditCore is greenfield (no
> production rows to migrate), the plan's nullable-then-backfill-then-NOT-NULL
> sequence and the Failed-row status-renumber `UPDATE` were unnecessary and were
> not implemented.

1. **`OutboxIdempotencyAndLeases` migration — idempotency (Phase 3)**
   - Add `IdempotencyKey` column directly as `NOT NULL` with `defaultValue: Guid.Empty` (schema-only, no backfill)
   - Add unique index `UX_AuditOutbox_IdempotencyKey`

2. **`OutboxIdempotencyAndLeases` migration — leases (Phase 4)**
   - Add `LeaseOwner` (NVARCHAR(100)), `LeaseExpiresAt` (DATETIMEOFFSET) columns
   - `Status` enum renumber (Failed 2→3) is a code-only change in `AuditOutboxEntity`; the column stays an `int`, so no data-update SQL
   - Drop `IX_AuditOutbox_Status_NextRetryAt_CreatedAt`, create `IX_AuditOutbox_Claimable` on `(Status, NextRetryAt, LeaseExpiresAt, CreatedAt)`

### Breaking Change Handling

- **Slice A:** `AuditEnvelope` gains `EnvelopeId`
  - Usually low-risk for consumers constructing envelopes via object initializers
  - Custom serializers, snapshot tests, and equality assertions may need updates
  - Producers must preserve `EnvelopeId` across retries and outbox serialization

- **Slices C-D:** Requires coordinated deployment
  - Run migrations before deploying new drainer code
  - Stop all drainers before the status mapping changes; old numeric enum mappings are not compatible merely because new columns are ignored.
  - Inventory/reconcile persisted states under the stopped-drainer procedure before rollback; H1 additionally requires every restarted drainer to use guarded transitions.
  - New drainer code requires new schema

- **Slice G:** If the public `PublishBatchAsync` return type changes
  - Any consumer implementing `IAuditSink` must update
  - Consumers only *calling* the method are usually low-friction to migrate
  - Apply the greenfield rule: update implementations and consumers together; do not add a legacy adapter/shim without a demonstrated requirement

### Suggested Release Shape

1. **Release 1** ✅ *Complete*
   - Slices A-B
   - Internal semantics normalized, no outbox schema change yet

2. **Release 2** ✅ *Slices complete*
   - Slices C-D
   - Idempotency + stateful outbox processing

3. **Release 3** ✅ *Slices complete*
   - Slices E-F
   - Processor extraction + observability

4. **Release 4 (only if needed)**
   - Slice G
   - Public API result contract

---

## Testing Strategy

### Unit Tests

| Component | Test Focus |
|-----------|------------|
| `AuditEnvelope` identity | `EnvelopeId` survives serialization / deserialization / retry paths |
| `BatchPublishResult` | Outcome aggregation, status counts |
| `AuditEntityBatchWriter` | Per-envelope outcome mapping, duplicate detection |
| `AuditEventBatchWriter` | Per-envelope outcome mapping, duplicate detection |
| `AuditBatchProcessor` | Kind routing, outcome merging, error classification |
| `AuditOutboxRepository` | Lease claim/release, state transitions |

### Integration Tests

| Scenario | Verification |
|----------|--------------|
| Envelope identity persistence | `EnvelopeId` survives interceptor → outbox → drainer path |
| Batch partial failure | Failed envelopes retry, succeeded don't |
| Explicit-event subset failure | No duplicate explicit events after batch error + retry |
| Duplicate envelope | Returns `Duplicate` status, no DB error |
| Lease expiry recovery | Crashed drainer's rows reclaimed |
| Concurrent drainers | No simultaneous valid claim by different owners; expired-lease replay is idempotent and stale owners cannot apply outcomes |
| DLQ routing | Failed rows reach DLQ with context |

### Load Tests (provisional; not yet qualified)

Use the workload definition and evidence requirements in the disposition table above.
These targets have no passing run attached to this plan.

| Metric | Target |
|--------|--------|
| Throughput | 10K envelopes/sec sustained |
| P99 latency | < 100ms from outbox write to sink commit |
| Memory | Stable under load (no unbounded growth) |

---

## Rollback Plan

Rollback requires schema and status-enum compatibility. A pre-D drainer interprets numeric
states differently; keeping the new columns alone does not make it compatible. Stop drainers
and explicitly reconcile states before such a rollback. H1 also requires all active drainers
to use guarded transitions; mixed old/new workers do not establish its safety guarantee.

Original slice rollback considerations:

1. **Slice A:** Keep `EnvelopeId`; it is additive and safe to retain
2. **Slice B:** Revert internal writer coordinator refactor; sink behavior unchanged
3. **Slice C:** Keep idempotency columns/indexes; revert code if needed
4. **Slice D:** Retain lease columns; reverting the status mapping requires stopped drainers and an explicit state conversion
5. **Slice E:** Merge processor back into drainer if extraction causes issues
6. **Slice F:** Remove metric instrumentation
7. **Slice G:** If shipped, coordinate consumer and package rollback for any signature change; no legacy compatibility adapter is planned

---

## Success Metrics (design goals; not benchmark evidence)

| Metric | Before | After |
|--------|--------|-------|
| Retry storms after batch failure | Common | Per-row retry routing delivered; sustained failure/load behavior remains H5 |
| Duplicate audit records from replay | Possible | Replay must not add duplicate logical records; verify mixed/partial/concurrent replay regressions |
| Drainer observability | Basic counters | Full histogram suite |
| Code complexity | Drainer coupled processing and persistence | Processor extraction complete; repository extraction deferred without a LOC target |
| Per-envelope failure visibility | None | Internal envelope/row outcomes implemented; public results deferred (Slice G) |
| Concurrent drainer safety | Distributed lock | Row claims delivered; guarded ownership transitions remain H1 |
| Batch/result correlation | Implicit ordering / ad-hoc row pairing | Stable `EnvelopeId` end-to-end |


### H1 source verification update — 2026-10-16

[H1 implementation and evidence](../evidence/audit-pipeline-2026-10/h1-ownership-2026-10-16/README.md)
records 299 passing tests and 19 expected provider skips, new ownership/commit-evidence/failure-
projection schema, and authorized source recovery. This does not implement H2/H3a/H3, qualify
H5 load targets, or establish that an existing host has loaded the new source.

### H1 local package release — 2026-10-16

[Dev.7](../evidence/audit-pipeline-2026-10/release-dev7/README.md) contains H1 and is published
to both local feeds after full Release verification: 2,536 passed, zero failed. Jesse will
commit this working tree before H2 begins. Host rollout and H3a/H3 remain separate work.

### H2 acceptance contract — 2026-10-16

[H2 verification](../evidence/audit-pipeline-2026-10/h2-acceptance-2026-10-16/README.md) records
62 passing focused tests and 2550 passing full Release regressions, zero failures. Both
sinks now reject null entries before writer calls; docs explain partial commits, interrupted
calls, stable-identity retries and consumer-transaction acceptance. No new package or host
deployment occurred. H3a/H3 were still planned at that historical checkpoint; subsequent verification is below.

### H3 exact outcomes — 2026-10-16

[H3 verification](../evidence/audit-pipeline-2026-10/h3-outcomes-2026-10-16/README.md) records
2,442 broad regression passes and 155 focused passes, zero failures, including real SQL mixed
races with RCSI off/on. Exact per-event outcomes, metrics, malformed-result recovery and bounded
contract-defect escalation are implemented. H2/H3a/H3 are now published locally as dev.8; host rollout, H4/H5 and
optional G remain separate. The [H3 contract](BatchPublishingH3Implementation.md) records the
required custom logger metadata and operational behavior.

### H3a/H3 post-review verification — 2026-10-16

[Review fixes and evidence](../evidence/audit-pipeline-2026-10/h3-review-fixes-2026-10-16/README.md)
close the receipt/source read race and late-conflict classification loss. Fourteen new SQL
cases, 322 focused tests and 2,443 broad regressions pass, zero final failures. Earlier
verification above is historical. No publication, schema or host rollout occurred.

### Local package delivery — dev.8

[Dev.8 release evidence](../evidence/audit-pipeline-2026-10/release-dev8/README.md) closes local
package delivery for reviewed H2/H3a/H3. Source commit `6f560a1`, 2,644 regression passes, 416
final committed-source passes, package-only consumer checks and both feed receipts are retained.
Host activation and its producer/authorization/migration/queue-data gates remain open.

### Current code and dev.9 acceptance — reviewed 2026-10-18

H1–H3 (including H3a) are implemented. [Dev.9](../evidence/audit-pipeline-2026-10/release-dev9/README.md)
corrects EF mutation identity; the rebuilt MillWorks host passed populated startup acceptance
(78 new rows/78 receipts) and all 54 focused host tests, zero failures/skips. Other producer,
authorization, queue/data, owning-database and drainer rollout gates remain open.

H1 already moved guarded persistence into the internal `OutboxStateStore`, registered with
per-operation context isolation. H4 is partly delivered; only its abstraction/orchestration-test
follow-up remains planned. H5 still lacks the required sustained end-to-end qualification;
G remains deferred. The [hardening code review](BatchPublishingHardening.md#code-review-reconciliation--2026-10-18)
records source evidence and the remaining gates.

### Producer/host gate inventory — 2026-10-18

The [current inventory and gate checks](../evidence/audit-pipeline-2026-10/rollout-review-2026-10-18/README.md)
record 14 explicit-event construction sites (all now supplying IDs), 11 missing producer
bindings, retry-provenance gaps, CommentThreads' missing host model composition, and absent
operator recovery/unsafe age-only failed-outbox cleanup integration. Existing compiled checks
passed 118 cases, zero failures/skips. Application SQL was unavailable, so actual migration,
queue/drainer and copied-data acceptance remain open. Jesse approved H4's small internal
interface over the existing state store; implementation and orchestration tests remain planned.

### Producer/host implementation and H4 — 2026-10-18

The previous inventory identified gaps now addressed in source. The [hardening plan](BatchPublishingHardening.md)
and [implementation evidence](../evidence/audit-pipeline-2026-10/rollout-implementation-2026-10-18/README.md)
track trusted producer grants, retained retry provenance, CommentThreads composition and
hold-authorized recovery/retention. H4 is implemented: `IOutboxStateStore` fronts the existing
per-operation store, with isolated drainer orchestration tests and preserved provider tests.
Queue-depth telemetry stays outside the mutation seam. Read-only application SQL verification
matches all 18 migrations and finds no queued rows/handoffs. New API compilation and runtime
acceptance remain open. H5 remains planned; G deferred.
