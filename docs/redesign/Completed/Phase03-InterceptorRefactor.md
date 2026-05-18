# Phase 03 — Interceptor → IAuditSink refactor

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase02-DefaultImmediateSink.md`](Phase02-DefaultImmediateSink.md)

## Goal

`AuditSaveChangesInterceptor` stops calling
`context.Set<AuditLogEntity>().Add(...)` directly. It builds `AuditEnvelope`
objects from change-tracker entries and publishes them via `IAuditSink`.
Persistence logic moves out of the interceptor into `IAuditEntityWriter`
(introduced in Phase 02).

Net behavior is unchanged for callers — the same audit rows land in the
same tables. The change is purely architectural: the interceptor becomes
a producer; the sink owns persistence.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Hot path.** This file (`AuditSaveChangesInterceptor.cs`) is on every
  `SaveChangesAsync` for every audited DbContext. Every change must be
  reviewed against the existing test suite before moving to the next
  change.
- **No silent semantic changes.** If the refactor changes anything beyond
  "where the row is constructed and persisted" (e.g., redaction order,
  fail-closed timing, FERPA enforcement timing, provider dispatch
  ordering), stop and ask before continuing.
- **Existing tests are the contract.** Any test failure in
  `tests/MillWorks.AuditCore.Tests/EntityFramework/` after this phase
  indicates an unintended semantic change — investigate, do not paper over.

## Files

| Action | Path | Purpose |
|---|---|---|
| Modified | `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs` | Replace direct `Set<AuditLogEntity>().Add` with `IAuditSink.PublishAsync` |
| Modified | `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Inject `IAuditSink` into the interceptor's factory |
| New | `tests/MillWorks.AuditCore.Tests/EntityFramework/InterceptorSinkRoutingTests.cs` | Verifies the interceptor publishes envelopes (does not write rows directly) |

No other files change. The `IAuditEntityWriter` from Phase 02 is the
landing point for the relocated persistence logic; do not introduce
additional indirection.

## Refactor outline

The interceptor today (lines ~439-641 of `AuditSaveChangesInterceptor.cs`)
does roughly:

```
ProcessAuditableEntries(ctx, entries) {
  try {
    var auditLogs = ctx.Set<AuditLogEntity>();
    foreach (entry in entries) {
      // ... build AuditLogEntity rows from entry.Properties ...
      auditLogs.Add(logEntry);
    }
  } catch (...) {
    // FailClosedForRegulated handling
  }
}
```

After this phase:

```
ProcessAuditableEntries(ctx, entries) {
  try {
    foreach (entry in entries) {
      var envelope = BuildEnvelope(entry, ctx);
      // PublishAsync is fire-and-await inside the interceptor —
      // see Decision D2 below.
      await sink.PublishAsync(envelope, cancellationToken);
    }
  } catch (...) {
    // FailClosedForRegulated handling — UNCHANGED
  }
}
```

`BuildEnvelope(entry, ctx)` is a new private method on the interceptor.
It contains the per-property diff logic, FERPA event-type tagging,
masking, and snapshot serialization that currently lives inline in
`ProcessAuditableEntries`. Extract this code; do not rewrite the logic.

## Decisions left to Jesse

1. **Sync vs async sink call inside interceptor.** EF interceptors run
   synchronously via `SavingChanges` and asynchronously via
   `SavingChangesAsync`. The current code branches on the latter. Should
   `IAuditSink.PublishAsync` be awaited inside the interceptor (current
   code does the equivalent for `auditLogs.Add`, which is sync), or should
   the sync `SavingChanges` path skip auditing as it does today (the
   README documents this — line 40 of `MillWorks.AuditCore/README.md`)?
   **Recommendation:** match current behavior — sync path skips audit;
   async path awaits the sink. Confirm.
2. **Envelope per-entry vs envelope per-change.** Current code creates
   ONE `AuditLogEntity` per changed property for `Modified` entries. Should
   the interceptor publish one envelope per entry (with
   `PropertyChanges` carrying the list) or one envelope per property?
   **Recommendation:** one envelope per entry — fewer sink calls, cleaner
   forensic story (one event = one entity change). The writer can still
   produce one row per changed property if that's the existing storage
   contract. Confirm — this changes semantics if anyone is querying by
   `AuditLogEntity` count.
3. **`AuditFailureMode` placement.** Currently the catch block in
   `ProcessAuditableEntries` (lines 604-640) handles fail-closed. After
   the refactor, sink-publish failures are caught at the same try/catch.
   Sink-side persistence failures (separate transaction in Phase 05+)
   need their own fail-closed wiring later. For this phase: keep the
   try/catch around the publish loop; the sink failures it sees are
   construction failures (envelope-build), which is the same failure
   class the original try/catch covered.
4. **`CaptureForProviderDispatch` and `DispatchProvidersAsync`.** These
   stay tied to `AuditApplicationDbContext` for now. Lifting them to work
   for any context is part of Phase 07 (drop AuditLogEntity coupling) /
   Phase 08 (Api wiring). Do NOT touch these methods in Phase 03.

## Verification

```bash
# After AuditSaveChangesInterceptor.cs is edited (one logical block at a time)
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~EntityFramework.AuditSaveChangesInterceptor"

# After MillWorksAuditBuilder.cs is updated
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj

# After InterceptorSinkRoutingTests.cs lands
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~InterceptorSinkRoutingTests"
```

Acceptance:
- All existing interceptor tests pass unchanged. The full
  `EntityFramework.*` test class set must be green.
- `InterceptorSinkRoutingTests` covers:
  - `Modified` entry → one publish; envelope has expected
    `PropertyChanges` for each modified property.
  - `Added` / `Deleted` entry → one publish; envelope has snapshot in
    `AdditionalData`.
  - FERPA-marked entity → envelope's description carries the `[FERPA]`
    prefix and `AdditionalData` carries FERPA metadata.
- `FailClosedForRegulated` integration tests still rethrow
  `AuditIntegrityException` when sink-publish throws.

## README impact

Phase 10 will rewrite the "Automatic Entity Auditing" section
(`MillWorks.AuditCore/README.md:39-40`) to mention the sink. Note in the
phase commit / PR description that this section is now stale; do NOT
edit the README in Phase 03.

## Out of scope

- Removing the early-return on missing `AuditLogEntity` → Phase 07.
- `IAuditContextSource` propagation (still casting to
  `AuditApplicationDbContext` for `CurrentUserId` etc.) → Phase 04.
- AuditDbContext rename / connection isolation → Phase 05.
- Outbox sink → Phase 06.

## Done when

- `AuditSaveChangesInterceptor.cs` no longer references
  `context.Set<AuditLogEntity>()` (verify with grep).
- `MillWorksAuditBuilder.cs` injects `IAuditSink` into the interceptor.
- `InterceptorSinkRoutingTests` is green.
- Full test suite is green.
- Phase doc updated with "Completed YYYY-MM-DD".

Completed 2026-04-26 — `AuditSaveChangesInterceptor` now publishes one `AuditEnvelope` per entry through `IAuditSink.PublishAsync` (resolved per-save via constructor-injected `IServiceScopeFactory`); private `BuildEnvelope(entry, ctx)` extraction keeps redaction order, FERPA tagging, and snapshot serialization fallback bit-for-bit identical. Modified-entry `Description` shifts to per-entity (`"Updated {Entity}"`) — the only deliberate semantic change; `PropertyName` column preserved. Phase 02 contract gap fixed first via E1 (writer applies `envelope.AdditionalData` per-row in the Modified fan-out, with new regression test). 7 pre-existing EF interceptor fixtures forklifted to build a real DI graph (sink + writer + shared in-memory DB name) so audit rows are visible to assertions. New 8-test `InterceptorSinkRoutingTests` pins the post-Phase-03 contract. Full unit suite: 1037 passed / 0 failed / 4 skipped (Phase 02 baseline 1028 + 8 routing tests + 1 writer regression).
