# Phase 07 — Drop AuditLogEntity coupling

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase06-OutboxSink.md`](Phase06-OutboxSink.md)

## Goal

Remove the early-return in `AuditSaveChangesInterceptor.GetAuditableEntries`
that bails when the saving DbContext's model lacks `AuditLogEntity`.
With the sink owning persistence (Phase 02-06), the interceptor no longer
needs the consumer's DbContext to map AuditCore entities. Consumer
DbContexts that have been mapping `AuditLogEntity` inline can stop —
their own `Phase 09` migration removes that block.

Also: lift the remaining `context as AuditApplicationDbContext` casts in
the interceptor — `ScopedServiceProvider`, `IsDispatchingProviders`,
`PendingProviderDispatches` — to work via documented contracts so
provider dispatch works for any audited DbContext.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Behavior change.** This phase changes the contract: any DbContext
  with the interceptor registered now produces audit rows, regardless of
  its model. That's the goal — it makes Phase 09 consumer cleanup
  possible. But it also means a previously-misconfigured consumer (e.g.,
  someone who attached the interceptor by accident) now gets audit
  rows. The startup-time misconfiguration check from the superseded plan
  (Item 01.D) is no longer needed — there is no misconfiguration to
  warn about. Remove the planning around it; do not introduce it.
- **The `IsAuditable` filter stays.** The `_auditEntityTypes` HashSet
  still excludes audit entity types from auditing themselves. That's
  the correct circular-dependency guard.

## Files

| Action | Path | Purpose |
|---|---|---|
| Modified | `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs` | Remove early-return; lift remaining casts |
| New | `src/MillWorks.AuditCore.Abstractions/Interfaces/IAuditProviderDispatchSource.cs` | Optional interface for consumer DbContexts that want provider dispatch |
| Modified | `src/MillWorks.AuditCore.EntityFramework/Data/AuditDbContext.cs` | Implement `IAuditProviderDispatchSource` |
| New | `tests/MillWorks.AuditCore.Tests/EntityFramework/BareConsumerDbContextTests.cs` | Verifies a DbContext with NO AuditCore entities + the interceptor produces audit rows |

## Refactor outline

### Step 1 — Remove the early-return

`AuditSaveChangesInterceptor.GetAuditableEntries` currently:

```csharp
private static List<EntityEntry>? GetAuditableEntries(DbContext? context)
{
    if (context == null) return null;

    if (context.Model.FindEntityType(typeof(AuditLogEntity)) == null)
        return null;  // ← REMOVE THIS BLOCK

    return context.ChangeTracker.Entries()
        .Where(...)
        .ToList();
}
```

Becomes:

```csharp
private static List<EntityEntry>? GetAuditableEntries(DbContext? context)
{
    if (context == null) return null;

    return context.ChangeTracker.Entries()
        .Where(static e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
        .Where(static e => !_auditEntityTypes.Contains(e.Entity.GetType()))
        .Where(static e => !HasNoAuditAttribute(e.Entity.GetType()))
        .ToList();
}
```

### Step 2 — Lift `ScopedServiceProvider` casts

Today `CaptureForProviderDispatch` and `DispatchProvidersAsync` cast to
`AuditApplicationDbContext` (now `AuditDbContext`) for
`ScopedServiceProvider`. New interface:

```csharp
namespace MillWorks.AuditCore.Abstractions.Interfaces;

public interface IAuditProviderDispatchSource
{
    /// <summary>
    /// Service provider for resolving audit providers. Null when the
    /// context is being used outside of a request scope.
    /// </summary>
    IServiceProvider? ScopedServiceProvider { get; }

    bool IsDispatchingProviders { get; set; }

    IReadOnlyList<PendingProviderDispatch>? PendingProviderDispatches { get; set; }
}
```

`AuditDbContext` implements it. Consumer DbContexts that want provider
dispatch implement it too (in Phase 09).

`CaptureForProviderDispatch` becomes:

```csharp
private static void CaptureForProviderDispatch(
    DbContext context,
    List<EntityEntry> auditableEntries)
{
    if (context is not IAuditProviderDispatchSource dispatchSource)
        return;

    if (dispatchSource.ScopedServiceProvider == null)
        return;

    // ... rest unchanged ...
}
```

`DispatchProvidersAsync` mirrors the pattern.

## Decisions left to Jesse

1. **`PendingProviderDispatch` placement.** This type currently lives
   inside `AuditApplicationDbContext`. To put it on the interface
   (Abstractions), it needs to move to Abstractions. **Recommendation:**
   move it. The type is a data carrier; it has no behavior and no EF
   coupling. Confirm.
2. **Interface name.** `IAuditProviderDispatchSource` is descriptive but
   long. Alternatives: `IAuditDispatchContext`, `IAuditProviderHost`.
   **Recommendation:** `IAuditProviderDispatchSource` — clearest.
   Confirm.
3. **Default behavior when no consumer DbContext implements
   `IAuditProviderDispatchSource`.** Provider dispatch silently
   no-ops. **Recommendation:** that's correct — providers are an
   opt-in extensibility point; libraries that don't want them just
   don't implement the interface. No warning, no exception.
4. **Should the interceptor's catch block also rethrow when sink-publish
   fails for a consumer DbContext that doesn't have the safety net of
   a transactional outbox?** Today it does (FailClosed semantics
   apply). After Phase 05, sink failure on `Immediate` mode is on the
   audit subsystem's connection — rethrowing rolls back the consumer's
   business write, which IS the FailClosed posture. Keep current
   behavior. No change needed; flagged to confirm.

## Verification

```bash
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj

# After BareConsumerDbContextTests lands
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~BareConsumerDbContextTests"

# Full SQL Server lane to catch interceptor regressions
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Integration.SqlServer"
```

Acceptance:
- All existing tests pass.
- `BareConsumerDbContextTests` covers:
  - A consumer-style `DbContext` with NO AuditCore entities mapped +
    interceptor registered → entity changes produce envelopes →
    `AuditLogEntity` rows land in the audit DbContext.
  - Same context implementing `IAuditContextSource` → user/correlation
    flow into the rows.
  - Same context implementing `IAuditProviderDispatchSource` →
    registered providers dispatch correctly.
- Provider dispatch tests still pass for `AuditDbContext`.

## README impact

Phase 10 will:
- Replace the "Automatic Entity Auditing" paragraph
  (`MillWorks.AuditCore/README.md:39-40`) with the accurate description:
  any DbContext with the interceptor produces audit rows, regardless of
  model.
- Document `IAuditContextSource` and `IAuditProviderDispatchSource` as
  the consumer-side contracts.
- Remove any mention of the inline `modelBuilder.Entity<AuditLogEntity>()`
  workaround pattern.

Note in commit/PR; do NOT edit README in this phase.

## Out of scope

- MillWorks Api wiring → Phase 08.
- Per-library cleanup → Phase 09.
- Removing the `IAuditDiagnostics` startup-time misconfiguration check
  hosted service — wait, that wasn't built in the first place. The
  superseded plan suggested it; the redesign makes it unnecessary.

## Done when

- `GetAuditableEntries` no longer checks for `AuditLogEntity`.
- `IAuditProviderDispatchSource` exists; `AuditDbContext` implements it.
- Interceptor uses the interface for provider dispatch (no remaining
  `context as AuditDbContext` casts for dispatch fields).
- `BareConsumerDbContextTests` green.
- Full test suite + SQL Server lane green.
- Phase doc updated with "Completed YYYY-MM-DD".
