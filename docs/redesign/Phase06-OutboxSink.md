# Phase 06 — Transactional outbox sink

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase05-AuditDbContextSeparation.md`](Phase05-AuditDbContextSeparation.md)

## Goal

For consumers that need audit failures to roll back business writes (the
`FailClosedForRegulated` posture), reintroduce shared-transaction
semantics via a transactional outbox pattern. `TransactionalOutboxSink`
writes a small `AuditOutboxEntity` row inside the saving consumer's
transaction. A background `AuditOutboxDrainer` reads outbox rows and
publishes them through `ImmediateSink` to the audit DbContext.

Net effect: regulated / zero-loss-durability consumers (HIPAA / FERPA /
PCI-DSS or any deployment whose posture requires that audit-subsystem
crashes never lose an in-flight envelope) get the durability + rollback
semantics they had before Phase 05, plus stronger guarantees (the outbox
row is the durable handoff; if the drainer crashes, on restart it picks
up from where it left off).

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **New entity table.** `AuditOutboxEntity` requires a migration. Migration
  is anchored to the `audit` schema (greenfield carve-out).
- **Reusing existing primitives.** `IntegrityWriteBatcher` and
  `IntegrityReconciliationService` (already in
  `src/MillWorks.AuditCore.Services/`) are the closest analog — review
  their patterns. Do NOT extract a shared base class; outbox semantics
  are different (per-row drain, not per-batch flush).
- **Consumer connection writes the outbox row.** This is the entire
  point — the outbox row commits with the consumer's `SaveChangesAsync`.
  That requires the outbox table to be reachable through the consumer's
  connection. Constraint: same physical database (already documented as
  the deployment posture in `MillWorks/README.md`).

## Files

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.EntityFramework/Entities/AuditOutboxEntity.cs` | Outbox row entity |
| Modified | `src/MillWorks.AuditCore.EntityFramework/Data/AuditDbContext.cs` | Map `AuditOutboxEntity` |
| New | `src/MillWorks.AuditCore.EntityFramework/Migrations/AddAuditOutbox.cs` (auto-gen via `dotnet ef migrations add`) | Schema for new table |
| New | `src/MillWorks.AuditCore.Services/Sinks/TransactionalOutboxSink.cs` | The outbox sink |
| New | `src/MillWorks.AuditCore.Services/Sinks/AuditOutboxDrainer.cs` | Background drainer (`BackgroundService`) |
| Modified | `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Sink-mode config + DI |
| Modified | `src/MillWorks.AuditCore.AspNetCore/Options/SecurityOptions.cs` (or wherever `AuditFailureMode` lives) | Add `AuditSinkMode` enum |
| New | `tests/MillWorks.AuditCore.Tests/Sinks/TransactionalOutboxSinkTests.cs` | Unit tests |
| New | `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/OutboxDrainerIntegrationTests.cs` | SQL Server end-to-end |

## Types introduced

### `AuditOutboxEntity`

```csharp
namespace MillWorks.AuditCore.EntityFramework.Entities;

[Table("AuditOutbox", Schema = "audit")]
public sealed class AuditOutboxEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    [Required] public string EnvelopeJson { get; set; } = string.Empty;

    [Required] public AuditOutboxStatus Status { get; set; } = AuditOutboxStatus.Pending;

    [Required] public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public int AttemptCount { get; set; }

    [MaxLength(2000)] public string? LastError { get; set; }
}

public enum AuditOutboxStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
}
```

The envelope is serialized to JSON for storage. Alternative: structured
columns. **Decision:** JSON — envelopes vary by kind, structured columns
would denormalize unnecessarily. The drainer deserializes, calls
`ImmediateSink.PublishAsync`, marks completed.

### `AuditSinkMode`

```csharp
public enum AuditSinkMode
{
    /// <summary>
    /// Audit writes happen on the audit-owned DbContext / connection.
    /// Decoupled from the consumer's transaction. Default.
    /// </summary>
    Immediate = 0,

    /// <summary>
    /// Audit writes participate in the consumer's transaction via outbox
    /// row. A background drainer commits the outbox row's payload to the
    /// audit DbContext after commit. Required for FailClosedForRegulated
    /// when business + audit must succeed atomically.
    /// </summary>
    TransactionalOutbox = 1,
}
```

DI registers `IAuditSink` based on `SecurityOptions.AuditSinkMode`.

### `TransactionalOutboxSink`

```csharp
namespace MillWorks.AuditCore.Services.Sinks;

public sealed class TransactionalOutboxSink(
    IAuditOutboxWriter outboxWriter,
    ILogger<TransactionalOutboxSink> logger) : IAuditSink
{
    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(envelope);
        await outboxWriter.WriteAsync(json, cancellationToken);
    }
}
```

`IAuditOutboxWriter` resolves the consumer DbContext (via a scoped
`IConsumerDbContextAccessor` or similar — see Decision D3) and adds the
`AuditOutboxEntity` row through it.

### `AuditOutboxDrainer`

`BackgroundService` that polls (or listens for) pending outbox rows,
deserializes each envelope, and calls `ImmediateSink.PublishAsync`.
On success: mark `Completed`. On failure: increment `AttemptCount`,
record `LastError`, leave as `Pending` for retry. After N attempts:
mark `Failed`, optionally route to DLQ.

## Decisions left to Jesse

1. **Reach the consumer DbContext from `TransactionalOutboxSink`.**
   Three options:
   a. **Scoped accessor pattern:** `IConsumerDbContextAccessor` is
      populated by the interceptor before publish; sink reads from it.
   b. **Pass the DbContext through the envelope:** add a non-serialized
      `DbContext` field. Ugly, but explicit.
   c. **Open a separate transaction-enlistment connection:** the outbox
      writer joins the ambient `TransactionScope`. Requires
      `TransactionScope` discipline at every consumer save site —
      brittle.
   **Recommendation:** (a). It matches the scoped-DI lifetime of the
   interceptor. Adds one new internal interface and one accessor
   implementation. Confirm.
2. **Drain cadence.** Polling interval, batch size, retry backoff.
   **Recommendation:** start with 250ms poll, 100-row batch, exponential
   backoff (1s, 5s, 30s, fail). Tune in Phase 11.
3. **Drainer leadership.** The existing `IAuditDistributedLockService`
   (Redis or in-memory) handles DLQ leader election. **Recommendation:**
   reuse it for the drainer leader. One worker drains at a time per
   replica set.
4. **Drainer DLQ on permanent failure.** Mark `Failed` only, or also
   route the envelope to the existing dead letter queue?
   **Recommendation:** route to DLQ — that's where the existing DLQ
   processor expects audit-write failures. Confirm.
5. **`FailClosedForRegulated` interaction.** Today the interceptor's
   catch block rethrows on sink-publish failure. With outbox sink, the
   sink's failure is "couldn't write outbox row." That still rolls back
   the consumer's transaction (the outbox row was supposed to commit
   with the consumer save). Verify with an integration test. No code
   change to the catch block.

## Verification

```bash
# After AuditOutboxEntity + DbContext mapping
dotnet ef migrations add AddAuditOutbox \
    --project src/MillWorks.AuditCore.EntityFramework \
    --startup-project src/MillWorks.AuditCore.EntityFramework  # or test host
dotnet build MillWorks.AuditCore.sln

# After TransactionalOutboxSink + Drainer + tests
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~TransactionalOutboxSink"
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~OutboxDrainerIntegration"
```

Acceptance:
- New `AuditOutbox` table exists in the migration set.
- `TransactionalOutboxSinkTests` covers: envelope JSON round-trip;
  outbox row added through the captured consumer DbContext;
  consumer rollback rolls back the outbox row.
- `OutboxDrainerIntegrationTests` covers: drainer picks up pending
  rows; calls `ImmediateSink`; marks completed; survives drainer
  restart with pending rows; retries on failure; routes to DLQ after N
  attempts.
- `FailClosedForRegulated` integration tests still pass under
  `AuditSinkMode.TransactionalOutbox`.

## README impact

Phase 10 will:
- Add an `AuditSinkMode` row to the Configuration table.
- Document the outbox table in the Database Schema section.
- Update Tamper Detection wording: explain Immediate vs Outbox modes
  and which one supports `FailClosedForRegulated`.

Note in commit/PR description; do NOT edit README in this phase.

## Out of scope

- Removing the consumer's `AuditLogEntity` model dependency → Phase 07.
- MillWorks Api integration → Phase 08.
- Per-library cleanup → Phase 09.

## Done when

- `AuditOutboxEntity` + migration land.
- `TransactionalOutboxSink` and `AuditOutboxDrainer` exist and DI selects
  via `AuditSinkMode`.
- Both sink modes have green test coverage.
- `FailClosedForRegulated` works under outbox mode.
- Phase doc updated with "Completed YYYY-MM-DD".

**Completed 2026-04-26**
