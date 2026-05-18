# Phase 05 — AuditDbContext separation

**Completed 2026-04-26**

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase04-AuditContextSource.md`](Phase04-AuditContextSource.md)

## Goal

Two changes in one phase, because they're inseparable:

1. **Rename** `AuditApplicationDbContext` → `AuditDbContext`. The
   "Application" suffix was a vestige of the single-DbContext era. The
   audit subsystem owns its own context; "Application" is misleading.
2. **Isolate** the sink's writer from the saving consumer DbContext. The
   `IAuditEntityWriter` (introduced in Phase 02) resolves a fresh scoped
   `AuditDbContext` from DI for every publish. Audit rows are written
   through the audit-owned context / connection — not through whatever
   DbContext is currently saving.

After this phase, audit writes are decoupled from the consumer
transaction. A consumer's `SaveChangesAsync` rollback no longer rolls
back the audit row. (Strict-mode coupling becomes opt-in via the
`TransactionalOutboxSink` in Phase 06.)

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Greenfield rename.** Do not add a forwarding `AuditApplicationDbContext`
  alias. Delete the old name; update every reference.
- **Behavior change documented up front.** This phase changes the
  failure semantics for any caller currently relying on
  audit-and-business-share-a-transaction. The Phase 06 outbox sink
  restores that option, but between Phase 05 and Phase 06 the codebase
  has only the decoupled behavior. Acceptable because tests cover both
  outcomes; production has not yet adopted Phase 05.
- **Migrations.** The `audit` schema and packaged migrations stay
  anchored to the default schema (greenfield carve-out from
  `feedback_greenfield_no_back_compat.md`). Renaming the DbContext does
  NOT regenerate migrations.

## Files

| Action | Path | Purpose |
|---|---|---|
| Renamed | `src/MillWorks.AuditCore.EntityFramework/Data/AuditApplicationDbContext.cs` → `AuditDbContext.cs` | Rename the type and file |
| Modified | All references to `AuditApplicationDbContext` across `src/` and `tests/` | Update name |
| Modified | `src/MillWorks.AuditCore.Services/Sinks/ImmediateSink.cs` (+ writer) | Resolve scoped `AuditDbContext` per publish |
| Modified | `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | DI registration uses new name |
| Modified | All EF migration `.cs` files that reference the type by name | Update name |
| New | `tests/MillWorks.AuditCore.Tests/Sinks/ImmediateSinkIsolationTests.cs` | Verifies audit writes happen on a separate context, survive consumer rollback |

A grep before starting will find every reference site:

```bash
grep -rn "AuditApplicationDbContext" \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/src \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/tests
```

The replacement count must match before/after. No partial rename.

## Refactor outline

### Rename

`sed`-style global rename with a manual review pass (the type appears in
class declarations, type parameters, DI registrations, comments, XML
docs, and migration `targetType` strings). Use Rider/IDE rename, not
text substitution, to catch all kinds.

### Sink isolation

`ImmediateSink` (Phase 02) currently has the writer reuse whatever
DbContext is saving. After this phase, the writer always resolves a
fresh `AuditDbContext` from DI. Sketch:

```csharp
internal sealed class AuditDbContextEntityWriter(
    IServiceScopeFactory scopeFactory) : IAuditEntityWriter
{
    public async Task WriteEntityChangeAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var ctx = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
        var entity = MapToAuditLogEntity(envelope);
        ctx.AuditLogs.Add(entity);
        await ctx.SaveChangesAsync(cancellationToken);
    }
}
```

`IServiceScopeFactory` is the right primitive: it gives the writer a
scope independent of the consumer's request scope, which means the
audit DbContext gets a fresh `DbContextOptions<AuditDbContext>` (own
connection per .NET DI defaults).

## Decisions left to Jesse

1. **Rename type-only or also rename the schema?** This phase keeps
   `Schema = "audit"`. **Recommendation:** keep. Schema rename is a
   breaking deployment change; the type rename is greenfield.
2. **`IServiceScopeFactory` vs `IDbContextFactory<AuditDbContext>`.**
   The factory pattern is built into EF Core 5+. **Recommendation:** use
   `IDbContextFactory<AuditDbContext>` if AuditCore already registers
   a pooled context factory; otherwise use `IServiceScopeFactory`.
   Pre-implementation check: grep for `AddPooledDbContextFactory` or
   `AddDbContextFactory` in `MillWorksAuditBuilder.cs`.
3. **Connection pooling implications.** A separate scope per audit publish
   means a separate connection acquisition per publish. For high-volume
   workloads this matters. **Recommendation:** out of scope for Phase 05;
   if Phase 11 endurance soak shows a problem, address it in a follow-up
   that batches publishes (or that's what the outbox sink in Phase 06 is
   for — high-volume should use the outbox).
4. **The sp_getapplock semantics.** Today the lock is taken inside the
   integrity-write path (`AuditIntegrityRepository.AcquireAppendLockAsync`).
   With audit writes on a separate connection, the lock is on that
   connection's transaction. Consumer transactions can no longer hold
   the lock. **This is correct** — the chain lock should be audit-owned,
   not consumer-owned. No change needed here; called out for awareness.
5. **`FailClosedForRegulated` semantics after this phase.** Today the
   interceptor's catch block rethrows when sink-publish fails, which
   rolls back the consumer's transaction. After this phase, sink-publish
   failure is "audit subsystem write failed" — rolling back the
   consumer's business write is still the right behavior for regulated
   entities, and the existing catch still does it. Verify with the
   existing FailClosed integration tests. No code change here.

## Verification

```bash
# After rename completes, confirm zero residual references:
grep -rn "AuditApplicationDbContext" \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/src \
    /Users/jesse/RiderProjects/MillWorks.AuditCore/tests
# Expected: no output

dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj

# After ImmediateSinkIsolationTests lands
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~ImmediateSinkIsolationTests"

# SQL Server lane (Docker required)
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.SqlServer"
```

### Failure model after this phase

The Phase 05 isolation changes which connection the audit row commits
on, but does NOT change the interceptor's failure-propagation contract.
Be precise about this — earlier drafts of this doc had a contradiction
that ChatGPT's review caught:

| Scenario | What happens | Why |
|---|---|---|
| Consumer save → audit-build success → audit-publish success → consumer commits | Both committed, decoupled across connections. | Happy path. |
| Consumer save → audit-build success → audit-publish success → consumer rolls back business txn | Audit row **survives** on the audit DbContext. | This is the behavior change introduced by Phase 05. The audit row is independently committed before the consumer's `SaveChangesAsync` returns. |
| Consumer save → audit-build throws → `Permissive` mode | Failure swallowed; consumer proceeds; no audit row. | Unchanged from current behavior. |
| Consumer save → audit-build throws → `FailClosedForRegulated` (regulated entity in batch) | Interceptor rethrows `AuditIntegrityException`; consumer rolls back business txn; no audit row. | Unchanged from current behavior. The interceptor's catch block (line 608 of `AuditSaveChangesInterceptor.cs`) rethrows; that propagates through `await sink.PublishAsync(...)` failures the same way it propagates through inline-construction failures today. |
| Consumer save → audit-build success → audit-publish throws → `FailClosedForRegulated` | Same as above — interceptor's catch sees the publish failure, rethrows. | The publish call lives inside the interceptor's `try` block, so sink-publish failures are visible to the catch. |
| Consumer save → audit-build success → audit-publish throws → `Permissive` | Failure swallowed; consumer proceeds; no audit row. | Mirrors above; permissive mode swallows publish failures the same as build failures. |

The "audit write survives consumer rollback" claim only applies to the
**success path** — once the audit row has independently committed on
the audit DbContext, a subsequent consumer rollback can't undo it.
This is desirable for forensic completeness (an attempted-and-rolled-back
write is still audit-worthy).

Acceptance:
- Zero `AuditApplicationDbContext` references in `src/` or `tests/`.
- All existing tests pass (rename is mechanical).
- `ImmediateSinkIsolationTests` covers:
  - **Success-path isolation:** consumer save → audit publishes → consumer
    rolls back business txn → audit row **survives** on the audit
    DbContext.
  - **Permissive failure isolation:** under `AuditFailureMode.Permissive`,
    audit-publish failure does NOT roll back consumer save (failure is
    swallowed; consumer proceeds).
- `FailClosedForRegulated` integration tests still pass — failure
  rollback works because the interceptor's catch block rethrows
  `AuditIntegrityException`, which the consumer's outer save sees. This
  applies to both audit-build failures (existing behavior) and
  audit-publish failures (new under sink isolation).

## README impact

Phase 10 will:
- Replace `AuditApplicationDbContext` references in
  `MillWorks.AuditCore/README.md` (Quick Start examples, Architecture
  diagram).
- Add a paragraph under "Tamper Detection" explaining that audit writes
  are isolated from consumer transactions by default (`AuditSinkMode.Immediate`);
  outbox sink (Phase 06, `AuditSinkMode.TransactionalOutbox`) is the
  opt-in for regulated / zero-loss-durability deployments
  (HIPAA / FERPA / PCI-DSS or any posture where audit-subsystem failures
  must not lose an in-flight envelope).
- Update the Architecture diagram (line ~247-263) — drop the implication
  that `AuditApplicationDbContext` is the application's own DbContext.

Note in commit/PR description that README is now stale; do NOT edit in
this phase.

## Out of scope

- Outbox sink → Phase 06.
- Removing the early-return on missing `AuditLogEntity` → Phase 07.
- Dropping the `MillWorks.AuditCore.EntityFramework` reference from
  consumer libraries → Phase 09.

## Done when

- File renamed; type renamed; all references updated.
- `ImmediateSink` writer resolves its own scoped `AuditDbContext`.
- `ImmediateSinkIsolationTests` green.
- Full test suite green (including SQL Server lane).
- Phase doc updated with "Completed YYYY-MM-DD".

Completed 2026-04-26 — `AuditApplicationDbContext` → `AuditDbContext` rename swept across 296 references in 103 .cs files (interceptor, sinks, writer, `MillWorksAuditBuilder`, `AuditModelCacheKeyFactory`, `DesignTimeDbContextFactory`, EF migrations, integration tests). Reference grep at completion: zero residual `AuditApplicationDbContext` mentions in `src/` or `tests/` .cs files (sole remaining mentions are in `Directory.Build.props` v1.6.2 release notes — already-shipped historical text, deliberately not edited). Namespace unchanged; no type forwarders. **Block 2 was a no-op against current code** — Phase 02's `AuditDbContextEntityWriter` already resolves a fresh scoped `AuditDbContext` per publish via `IServiceScopeFactory.CreateAsyncScope()`, exactly matching Phase 05's target design. D2 spec recommendation to consider `IDbContextFactory<AuditDbContext>` was checked against the live builder: no `AddPooledDbContextFactory` / `AddDbContextFactory` registration exists in `src/`, confirming `IServiceScopeFactory` is the right primitive. D4 (`sp_getapplock` semantics): the chain lock now lives on the audit-owned connection's transaction — correct, no code change. D5 (`FailClosedForRegulated` after isolation): interceptor's catch rethrows `AuditIntegrityException` on sink-publish failure, propagating through the consumer's outer save — no code change, verified by existing FailClosed sqlite integration tests still passing. New `tests/MillWorks.AuditCore.Tests/Sinks/ImmediateSinkIsolationTests.cs` covers two acceptance scenarios: `SuccessPath_AuditRowSurvivesConsumerRollback` (consumer rolls back business txn after audit publishes; audit row remains on the audit DbContext) and `PermissiveFailure_SinkThrows_DoesNotRollBackConsumerSave` (under `AuditFailureMode.Permissive` a sink-publish throw is swallowed; consumer save proceeds). Full unit suite green: 1050 passed / 0 failed / 4 skipped (Phase 04.5 baseline 1048 + 2 isolation tests). SQL Server lane: 19/19 FailClosed integration tests pass after deleting the stale `AuditInterceptorFailClosedSqlServerTests.SaveChangesAsync_SuccessPath_BusinessAndAuditRowsCommitInSameTransaction` test (red on `main` since Phase 02 — its "audit row commits in the same SQL Server transaction as the business row" assertion contradicted the locked `AuditSinkMode.Immediate` posture, which by design commits the audit row on a separate connection; the asserted behavior is structurally impossible after Phase 02's `IServiceScopeFactory.CreateAsyncScope()` writer). Behavior change documented in `CHANGELOG.md`'s `[Unreleased]` Breaking Changes: by default audit row survives consumer rollback; `TransactionalOutboxSink` (Phase 06) restores opt-in shared-transaction durability for fail-closed regulated postures. README staleness deferred to Phase 10 (`MillWorks.AuditCore/README.md` Quick Start + Architecture diagram still reference `AuditApplicationDbContext`; deliberately untouched per the phase's `Out of scope`). Version stays `2.0.0-preview` (no bump from Phase 04.5; same preview train).
