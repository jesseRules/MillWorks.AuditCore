# Batch Publishing Redesign Plan

**Goal:** Transform batch publishing from "best effort" convenience into an explicit, transactional contract with per-envelope result tracking and deterministic outbox processing.

---

## Current State Summary

| Aspect | Current Behavior | Problem |
|--------|------------------|---------|
| `PublishBatchAsync` return | `Task` (void) | Caller cannot distinguish which envelopes succeeded/failed |
| EntityChange handling | Buffered, single `SaveChangesAsync` | Atomic but no per-row feedback |
| ExplicitEvent handling | Drainer isolates them and processes one-at-a-time; sink still writes them inline | Common-case duplicate hazard is mitigated, but sink semantics remain mixed |
| Outbox row states | `Pending=0, Completed=1, Failed=2` | No `InFlight` claim; no lease ownership |
| Idempotency | `AuditEvent.EventId` exists for explicit events; entity-change envelopes have no first-class envelope identity | Result correlation and replay semantics depend on ad-hoc mapping |
| Drainer recovery | EntityChange rows batch first, then fall back one-at-a-time; ExplicitEvent rows are isolated up front | Safer than the original design, but still not a deterministic per-row outcome contract |

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
    public bool AllSucceeded => Outcomes.All(o => o.Status == EnvelopeStatus.Succeeded);
    public int SucceededCount => Outcomes.Count(o => o.Status == EnvelopeStatus.Succeeded);
    public int FailedCount => Outcomes.Count(o => o.Status == EnvelopeStatus.Failed);
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
- Outbox write is atomic; if it fails, all envelopes failed

### 1.6 Update AuditOutboxDrainer

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

- Use `BatchPublishResult.Outcomes` to map success/failure back to outbox rows
- Preserve `(OutboxRowId, EnvelopeId)` correlation inside the drainer/processor layer;
  sinks must not know outbox row ids
- Eliminate blind fallback to one-at-a-time (only retry actual failures)
- Match outcomes back to rows by `EnvelopeId`

**Acceptance Criteria:**
- [ ] `AuditEnvelope` has a stable `EnvelopeId`
- [ ] `PublishBatchAsync` returns `BatchPublishResult`
- [ ] Drainer uses outcomes to update only failed rows
- [ ] Duplicate detection returns `Duplicate` status, not exception
- [ ] All existing tests updated and passing

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
- [ ] No inline side effects during batch iteration
- [ ] Both entity changes and explicit events batch-written
- [ ] Coordinator combines outcomes from both writers
- [ ] Drainer unchanged (consumes same `BatchPublishResult`)

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
- [ ] Unique constraint on `AuditLog.EventId`
- [ ] Unique constraint on `AuditOutbox.IdempotencyKey`
- [ ] Duplicate writes return success, not throw
- [ ] Drainer replay is safe (no duplicate audit records)
- [ ] Metrics track duplicate counts

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
// Reset to Pending with incremented AttemptCount
```

### 4.6 Update Drainer Query Index

**Migration:**
```sql
DROP INDEX IX_AuditOutbox_Status_NextRetryAt_CreatedAt;

CREATE INDEX IX_AuditOutbox_Claimable ON [audit].[AuditOutbox] 
(Status, NextRetryAt, LeaseExpiresAt, CreatedAt)
INCLUDE (EnvelopeJson, EnvelopeVersion, IdempotencyKey);
```

**Acceptance Criteria:**
- [ ] Rows transition: Pending → InFlight → Completed/Failed
- [ ] Lease columns populated during claim
- [ ] Expired leases recovered automatically
- [ ] No row processed by multiple drainers simultaneously
- [ ] Metrics track InFlight count and lease recovery rate

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

### 5.4 Extract Outbox Repository

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxRepository.cs`

- `ClaimBatchAsync(leaseId, expiry, batchSize)`
- `CompleteRowAsync(rowId)`
- `ScheduleRetryAsync(rowId, retryAfter)`
- `FailRowAsync(rowId, errorMessage)`
- `RecoverExpiredLeasesAsync()`

**Acceptance Criteria:**
- [ ] Drainer is ~100 lines (orchestration only)
- [ ] Processor handles all sink interaction
- [ ] Repository encapsulates row state transitions
- [ ] Each component independently testable
- [ ] Processor unit tests don't need DbContext

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

### 6.3 Add Queue Depth Observable

**File:** `MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs`

```csharp
// Register on startup
AuditMetrics.Meter.CreateObservableGauge(
    "audit.outbox.pending_count",
    () => _outboxRepository.GetPendingCountAsync().GetAwaiter().GetResult());

AuditMetrics.Meter.CreateObservableGauge(
    "audit.outbox.oldest_pending_age_seconds",
    () => _outboxRepository.GetOldestPendingAgeAsync().GetAwaiter().GetResult()
           .TotalSeconds);
```

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
- [ ] Batch size histogram by envelope kind
- [ ] Success/failure/duplicate counters by kind
- [ ] Retry count metric
- [ ] DLQ routing counter
- [ ] Lease recovery counter
- [ ] Queue depth gauge (pending + inflight)
- [ ] Oldest pending row age gauge
- [ ] Row age histogram at drain time
- [ ] Error classification tags

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

| Slice | Scope | Depends On | Public Break? | Est. Effort |
|-------|-------|------------|---------------|-------------|
| A | Add `AuditEnvelope.EnvelopeId`; preserve through interceptor, immediate sink, outbox serialization, drainer deserialization | None | Yes: `AuditEnvelope` contract | 1 day |
| B | Introduce internal `WriteOutcome` / batch writer abstractions for entity changes and explicit events; refactor `ImmediateSink` to coordinate them behind the current API | A | No | 2-3 days |
| C | Add explicit-event idempotency constraints and outbox `IdempotencyKey`; handle duplicates as success | A | Yes: migration | 1-2 days |
| D | Add stateful outbox row claims (`InFlight`, lease columns, repository methods, claim/apply transitions) | C | Yes: migration | 2-3 days |
| E | Extract `IAuditBatchProcessor`; make drainer orchestration-only | B, D | No | 1-2 days |
| F | Add metrics / observability | E | No | 1 day |
| G | Change public `IAuditSink.PublishBatchAsync` to return `BatchPublishResult` if internal processing still cannot expose needed visibility without it | B, C, D, E | Yes: public API | 1-2 days |

**Realistic total:** ~8-14 engineering days depending on migration/test overhead.

### Recommendation

Treat **Slice G** as optional until the end.

If deterministic replay, idempotency, and row-level outbox outcomes can be fully
implemented behind internal abstractions, avoid the public signature break in
the first release of this redesign. A top-tier library should minimize public
surface churn unless the new API materially unlocks consumer value.

---

## Migration Strategy

### Database Migrations

1. **Phase 3 Migration** (`AddIdempotencyKey`)
   - Add `IdempotencyKey` column (nullable initially)
   - Backfill from existing envelope JSON
   - Add unique index
   - Make column NOT NULL

2. **Phase 4 Migration** (`AddLeaseColumns`)
   - Add `LeaseOwner`, `LeaseExpiresAt` columns
   - Update `Status` enum values (2→3 for Failed)
   - Drop old index, create new covering index

### Breaking Change Handling

- **Slice A:** `AuditEnvelope` gains `EnvelopeId`
  - Usually low-risk for consumers constructing envelopes via object initializers
  - Custom serializers, snapshot tests, and equality assertions may need updates
  - Producers must preserve `EnvelopeId` across retries and outbox serialization

- **Slices C-D:** Requires coordinated deployment
  - Run migrations before deploying new drainer code
  - Old drainer code compatible with new schema (ignores new columns)
  - New drainer code requires new schema

- **Slice G:** If the public `PublishBatchAsync` return type changes
  - Any consumer implementing `IAuditSink` must update
  - Consumers only *calling* the method are usually low-friction to migrate
  - Consider shipping an adapter/shim release if external implementations are expected

### Suggested Release Shape

1. **Release 1**
   - Slices A-B
   - Internal semantics normalized, no outbox schema change yet

2. **Release 2**
   - Slices C-D
   - Idempotency + stateful outbox processing

3. **Release 3**
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
| Concurrent drainers | No row processed twice |
| DLQ routing | Failed rows reach DLQ with context |

### Load Tests

| Metric | Target |
|--------|--------|
| Throughput | 10K envelopes/sec sustained |
| P99 latency | < 100ms from outbox write to sink commit |
| Memory | Stable under load (no unbounded growth) |

---

## Rollback Plan

Each phase is independently deployable and rollback-safe:

1. **Slice A:** Keep `EnvelopeId`; it is additive and safe to retain
2. **Slice B:** Revert internal writer coordinator refactor; sink behavior unchanged
3. **Slice C:** Keep idempotency columns/indexes; revert code if needed
4. **Slice D:** Keep lease columns/status values; revert code if needed
5. **Slice E:** Merge processor back into drainer if extraction causes issues
6. **Slice F:** Remove metric instrumentation
7. **Slice G:** If shipped, provide compatibility adapter or revert public return-type change in a major-version rollback only

---

## Success Metrics

| Metric | Before | After |
|--------|--------|-------|
| Retry storms after batch failure | Common | Eliminated |
| Duplicate audit records from replay | Possible | Impossible |
| Drainer observability | Basic counters | Full histogram suite |
| Code complexity (drainer LOC) | ~350 | ~100 |
| Per-envelope failure visibility | None | Full |
| Concurrent drainer safety | Distributed lock | Row-level leases |
| Batch/result correlation | Implicit ordering / ad-hoc row pairing | Stable `EnvelopeId` end-to-end |
