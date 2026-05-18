# Phase 01 — IAuditSink abstraction

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)

## Goal

Introduce the `IAuditSink` contract and `AuditEnvelope` DTO in
`MillWorks.AuditCore.Abstractions`. No implementations, no callers, no
behavior change. The phase ends with new types compiling and one
construction-only unit test passing.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply:

1. Plan is spec — only the files named below change.
2. No backwards-compat shims — types are net-new; no obsolete forwarders.
3. Build + test after every file change. No batching.
4. List unresolved decisions before editing — see "Decisions" below.
5. Ambiguity is a stop, not a permission.

## Files

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Abstractions/Interfaces/IAuditSink.cs` | The sink contract |
| New | `src/MillWorks.AuditCore.Abstractions/Models/AuditEnvelope.cs` | DTO carried into the sink |
| New | `src/MillWorks.AuditCore.Abstractions/Enums/AuditEnvelopeKind.cs` | Discriminator |
| New | `src/MillWorks.AuditCore.Abstractions/Models/AuditEnvelopePropertyChange.cs` | Per-property change record (used when `Kind = EntityChange`) |
| New | `tests/MillWorks.AuditCore.Tests/Abstractions/AuditEnvelopeTests.cs` | Construction tests |

No existing files are modified. No DI registration (the default sink lands
in Phase 02).

## Types introduced

### `AuditEnvelopeKind`

```csharp
namespace MillWorks.AuditCore.Abstractions.Enums;

public enum AuditEnvelopeKind
{
    /// <summary>
    /// Captured by AuditSaveChangesInterceptor from EF change-tracker entries.
    /// Carries property-level OldValue/NewValue diffs.
    /// </summary>
    EntityChange = 0,

    /// <summary>
    /// Explicit application-level event raised via IAuditLogger.LogAsync.
    /// Carries an EventType + AdditionalData payload.
    /// </summary>
    ExplicitEvent = 1,
}
```

### `AuditEnvelopePropertyChange`

```csharp
namespace MillWorks.AuditCore.Abstractions.Models;

public sealed record AuditEnvelopePropertyChange(
    string PropertyName,
    string? OldValue,
    string? NewValue);
```

Stored as a list on the envelope. `OldValue`/`NewValue` are already
masked / redacted by the producer (the interceptor or the explicit
caller); the sink does not re-mask. This matches the current contract
where `MaskOrRedact` runs in the interceptor.

### `AuditEnvelope`

```csharp
namespace MillWorks.AuditCore.Abstractions.Models;

using MillWorks.AuditCore.Abstractions.Enums;

public sealed record AuditEnvelope
{
    public required AuditEnvelopeKind Kind { get; init; }
    public required string EntityName { get; init; }
    public required AuditAction Action { get; init; }
    public Guid? EntityId { get; init; }
    public string? UserId { get; init; }
    public string? CorrelationId { get; init; }
    public string? IpAddress { get; init; }
    public string? UserAgent { get; init; }
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;

    // EntityChange-only fields
    public IReadOnlyList<AuditEnvelopePropertyChange>? PropertyChanges { get; init; }

    // ExplicitEvent-only fields
    public string? EventType { get; init; }
    public string? AdditionalData { get; init; }

    // Optional, both kinds
    public string? Description { get; init; }
}
```

The envelope deliberately uses primitives (`string`, `Guid`, `DateTimeOffset`)
and AuditCore enums (`AuditAction`). It does not reference any EF type.

### `IAuditSink`

```csharp
namespace MillWorks.AuditCore.Abstractions.Interfaces;

using MillWorks.AuditCore.Abstractions.Models;

public interface IAuditSink
{
    /// <summary>
    /// Publish an audit envelope to the sink. The sink decides where and
    /// when to commit it (immediate, transactional outbox, batched, etc.).
    /// </summary>
    /// <param name="envelope">The envelope to publish. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the sink has accepted the envelope.</returns>
    /// <remarks>
    /// "Accepted" does not mean "committed to the audit store" — for outbox
    /// sinks, it means "committed to the outbox." The audit subsystem is
    /// responsible for durability semantics; callers MUST NOT assume the
    /// envelope is queryable when this method returns.
    /// </remarks>
    Task PublishAsync(AuditEnvelope envelope, CancellationToken cancellationToken = default);
}
```

The contract is deliberately minimal. Batch publish, query, and verification
operations live on other interfaces (`IAuditLogger`, `IAuditQueryService`,
`ITamperDetectionService`) and remain unchanged in this phase.

## Decisions left to Jesse

1. **`AuditEnvelopeKind` value count.** The enum starts with `EntityChange`
   and `ExplicitEvent`. Should there be a third value for security events
   (currently written via `AuditSecurityEventEntity`), or do those stay on
   their own dedicated path? **Recommendation:** keep security events on the
   dedicated path for now — they're heavily-typed (severity, status, related
   event ID) and conflating them with entity-change envelopes adds optional
   fields that are mostly unused. Security events can become a third
   envelope kind in a future phase if needed.
2. **Envelope mutability.** I've used `record` with `init` setters (immutable
   after construction). Alternative: a `class` with public setters, so
   producers can build incrementally. **Recommendation:** keep immutable —
   envelopes cross subsystem boundaries and shared mutability invites bugs.
3. **`OccurredAt` timezone.** Using `DateTimeOffset` (UTC by default
   convention). No alternative under consideration.
4. **PropertyChanges null vs empty.** When `Kind = EntityChange`, is
   `PropertyChanges = null` valid (no diffs computed) or must it always be
   non-null? **Recommendation:** allow null — matches the existing
   interceptor behavior where Added/Deleted entries don't carry per-property
   diffs.

## Verification

After each file is added (one at a time):

```bash
dotnet build MillWorks.AuditCore.sln
```

After all five files exist, the test:

```bash
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Abstractions.AuditEnvelopeTests"
```

Acceptance:
- `dotnet build` clean (no new warnings).
- `AuditEnvelopeTests` passes — covers: minimum-required-fields construction
  for both kinds, immutability (record with-expression behavior),
  `PropertyChanges` defaults to null.

## README impact

None for this phase — `IAuditSink` is not user-visible until Phase 02
registers a default implementation. Phase 10 will document it as part of
the architecture rewrite.

## Out of scope

- Any implementation of `IAuditSink` → Phase 02.
- Any caller (interceptor, logger) routing through `IAuditSink` → Phase 03.
- DI registration → Phase 02.
- Cross-DbContext context propagation → Phase 04.

## Done when

- 4 new source files exist under `src/MillWorks.AuditCore.Abstractions/`.
- 1 new test file exists, all assertions green.
- `dotnet build MillWorks.AuditCore.sln` is clean.
- No other file in the repo is modified.
- This phase doc is updated with a one-line "Completed YYYY-MM-DD" note.

Completed 2026-04-26 — `IAuditSink`, `AuditEnvelope`, `AuditEnvelopeKind`, and `AuditEnvelopePropertyChange` added to `MillWorks.AuditCore.Abstractions`; `AuditEnvelopeTests` covers construction, immutability, and equality (9 tests, green).
