# Phase 08 — MillWorks.Api bridge wiring

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase07-DropEntityCoupling.md`](Phase07-DropEntityCoupling.md)

## Goal

In `/Users/jesse/RiderProjects/MillWorks/`: extend the existing
`AuditBridge` to implement `IAuditPublisher` for all 9 audited
libraries; uniformize interceptor attachment so every audited DbContext
is wired through Api (no library-side self-attach). After this phase,
one place in MillWorks knows which DbContexts are audited.

## Current state (verified by survey, 2026-04-25)

The earlier Phase 08 draft assumed every library exposed an
`Action<IServiceProvider, DbContextOptionsBuilder>` overload that the
Api had to populate. That is **not** the live state. The actual wiring
patterns across the 9 audited libraries:

| Library | Interceptor pattern | Audit code in DbContext |
|---|---|---|
| Compliance | Pattern 1 — Api-central lambda (`Program.cs:270-277`) | Inline `AuditLogEntity` mapping (`ComplianceDbContext.cs:99-103`) + `UseFieldEncryption` |
| Identity | Pattern 1 — Api-central lambda (`Program.cs:192-201`) | None |
| DataProcessing | **Pattern 3 — Library self-attach via constructor injection + `OnConfiguring`** (`DataProcessingDbContext.cs:26-39`) | None |
| Notification | Pattern 1 (Api-central) | None |
| SqlBuilder | Pattern 1 | None |
| Document | Pattern 1 | None |
| Media | Pattern 1 | None |
| Git | Pattern 1 | None |
| Ai | Pattern 1 | None |

So 8 of 9 are already in the right shape; only DataProcessing has a
library-side self-attach pattern that needs to move to Api. That move
is small but it is library code — Phase 08 is not strictly Api-only.

`AuditBridge` already exists at
`/Users/jesse/RiderProjects/MillWorks/MillWorks.Api/Bridge/Audit/AuditBridge.cs`,
registered in `BridgeServiceExtensions.cs:528`. It currently implements
only `IFinanceAuditService` and calls `IAuditLogger.LogAsync` directly.
Phase 08 extends it.

`SecurityBridge` exists at
`/Users/jesse/RiderProjects/MillWorks/MillWorks.Api/Bridge/Security/SecurityBridge.cs`
and is a security/encryption bridge — not the audit-publisher. The
earlier draft of this doc treated `SecurityBridge` as the candidate for
audit fan-out; that was based on the MillWorks README's brief mention
of "audit logging" in the SecurityBridge entry, but the actual code
already chose a different split (audit gets its own bridge). Phase 08
follows the codebase's choice.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Cross-repo phase.** This phase touches
  `/Users/jesse/RiderProjects/MillWorks/`, not the AuditCore repo.
  Get explicit Jesse go-ahead before any `Edit` or `Write` outside
  `/Users/jesse/RiderProjects/MillWorks.AuditCore/`.
- **DataProcessing requires library code touch.** The DbContext
  constructor + `OnConfiguring` get pruned in this phase. Acknowledged
  scope creep from the original "Api-only" framing; corrected after
  the survey. The 8 other libraries stay untouched in Phase 08
  (Compliance's inline `AuditLogEntity` mapping is removed in Phase 09).
- **`AuditBridge` is the audit-publish landing point.** Do NOT add
  audit publishing to `SecurityBridge`. Do NOT create a third bridge.
  The codebase already chose `AuditBridge`; the redesign extends it.

## Files (in `/Users/jesse/RiderProjects/MillWorks/`)

| Action | Path | Purpose |
|---|---|---|
| Modified | `MillWorks.Api/Bridge/Audit/AuditBridge.cs` | Implement `IAuditPublisher` (assumes D3 = shared interface in Abstractions); keep existing `IFinanceAuditService` implementation, internally route both through `IAuditSink` |
| Modified | `MillWorks.Api/Extensions/BridgeServiceExtensions.cs` | Update bridge registration to expose `IAuditPublisher` from the same instance |
| Modified | `MillWorks.Api/Program.cs` | Centralize the DataProcessing interceptor attachment (currently relies on the constructor-injection pattern) |
| Modified | `MillWorks.DataProcessing/Data/DataProcessingDbContext.cs` | Drop the constructor overload that takes `AuditSaveChangesInterceptor`; drop `OnConfiguring`; revert to the single `(DbContextOptions<DataProcessingDbContext>)` constructor |
| Modified | `MillWorks.DataProcessing/MillWorks.DataProcessing.csproj` | Drop `MillWorks.AuditCore.EntityFramework` package reference (no longer needed once the interceptor injection is gone — relies on Phase 04.5 having moved attributes to Abstractions). Switch to `MillWorks.AuditCore.Abstractions` if not already there. |

## Refactor outline

### 1. Extend `AuditBridge`

Today (`AuditBridge.cs`):

```csharp
public class AuditBridge(
    IAuditLogger auditLogger,
    IAuditQueryService queryService,
    ILogger<AuditBridge> logger)
    : IFinanceAuditService
{
    async Task IFinanceAuditService.LogFinancialEventAsync(...)
    {
        await auditLogger.LogAsync(eventType, $"Finance.{entityType}", data, ct);
    }
    // ... two more IFinanceAuditService methods ...
}
```

After Phase 08:

```csharp
public class AuditBridge(
    IAuditSink auditSink,           // ← swapped from IAuditLogger
    IAuditQueryService queryService,
    ILogger<AuditBridge> logger)
    : IFinanceAuditService, IAuditPublisher
{
    // IAuditPublisher — primary surface for new audit-publish callers.
    public Task PublishAsync(AuditEnvelope envelope, CancellationToken ct)
        => auditSink.PublishAsync(envelope, ct);

    // IFinanceAuditService — preserved for existing Finance callers.
    // Internally builds an envelope and routes through the same sink.
    async Task IFinanceAuditService.LogFinancialEventAsync(string eventType, Guid entityId, string entityType,
        Dictionary<string, object?>? metadata, CancellationToken ct)
    {
        var envelope = new AuditEnvelope
        {
            Kind = AuditEnvelopeKind.ExplicitEvent,
            EventType = eventType,
            EntityName = $"Finance.{entityType}",
            EntityId = entityId,
            AdditionalData = metadata is null ? null : JsonSerializer.Serialize(metadata),
        };
        await auditSink.PublishAsync(envelope, ct);
    }

    // ... LogPaymentEventAsync, GetAuditTrailAsync, ExportAuditTrailAsync similarly ...
}
```

Two things change:
- `IAuditLogger` → `IAuditSink` injection.
- New `IAuditPublisher` interface implemented; existing
  `IFinanceAuditService` preserved.

### 2. Bridge registration

`BridgeServiceExtensions.cs:530` already registers `AuditBridge` with
`AddScoped` (scoped lifetime — one instance per request scope, matches
the rest of the bridge taxonomy). Add the `IAuditPublisher` mapping
from the same scoped instance:

```csharp
services.AddScoped<AuditBridge>();
services.AddScoped<IFinanceAuditService>(static sp => sp.GetRequiredService<AuditBridge>());
services.AddScoped<IAuditPublisher>(static sp => sp.GetRequiredService<AuditBridge>());  // ← new
```

Do NOT change the lifetime to singleton — `IAuditSink` (which
`AuditBridge` injects) is itself scoped (`ImmediateSink` / `TransactionalOutboxSink`
both resolve a scoped `AuditDbContext` or scoped writer per the Phase 02 / 05
specs). A singleton bridge holding a scoped sink would capture a stale scope.

### 3. DataProcessing self-attach removal

Today (`DataProcessingDbContext.cs:15-39`):

```csharp
private readonly AuditSaveChangesInterceptor? _auditInterceptor;

public DataProcessingDbContext(DbContextOptions<DataProcessingDbContext> options) : base(options) { }

public DataProcessingDbContext(
    DbContextOptions<DataProcessingDbContext> options,
    AuditSaveChangesInterceptor? auditInterceptor) : base(options)
{
    _auditInterceptor = auditInterceptor;
}

protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
{
    base.OnConfiguring(optionsBuilder);
    if (_auditInterceptor is not null)
        optionsBuilder.AddInterceptors(_auditInterceptor);
}
```

After:

```csharp
public DataProcessingDbContext(DbContextOptions<DataProcessingDbContext> options) : base(options) { }

// OnConfiguring deleted; constructor overload deleted; field deleted.
```

`Program.cs` adds the central registration (matches the Compliance/
Identity pattern):

```csharp
services.AddDbContext<MillWorks.DataProcessing.Data.DataProcessingDbContext>((sp, options) =>
{
    ConfigureDbContext(options);
    var auditInterceptor =
        sp.GetService<MillWorks.AuditCore.EntityFramework.Interceptors.AuditSaveChangesInterceptor>();
    if (auditInterceptor is not null)
        options.AddInterceptors(auditInterceptor);
});
```

## Decisions left to Jesse

1. **D3 (per-library `I{Library}AuditPublisher` vs shared
   `IAuditPublisher`).** This phase assumes the shared-interface answer
   (one `IAuditPublisher` in Abstractions). If you pick per-library
   instead, `AuditBridge` implements 9 nearly-identical interfaces
   instead of 1. Either is mechanical; the doc above shows the shared
   approach.
2. **`IFinanceAuditService` preservation.** The bridge currently
   exposes Finance-specific shapes (`LogFinancialEventAsync`,
   `LogPaymentEventAsync`, `GetAuditTrailAsync`,
   `ExportAuditTrailAsync`). Should the redesign also flip Finance over
   to `IAuditPublisher` directly (delete the Finance-specific surface)?
   **Recommendation:** preserve `IFinanceAuditService` — it's a
   library-defined local abstraction with PCI-DSS-flavored shapes.
   The bridge implements both. Confirm.
3. **DataProcessing migration timing.** This phase touches
   DataProcessing library code. Two options:
   a. Do it here (Phase 08) so the Api-side wiring can be uniform.
   b. Defer to Phase 09 (where all library cleanup happens) and accept
      Phase 08 leaving DataProcessing on its self-attach pattern
      temporarily.
   **Recommendation:** (a) — without it, Phase 08 can't centralize
   DataProcessing's wiring in Api, and the redesign's "one place
   knows which DbContexts are audited" goal isn't achieved. Confirm.
4. **`AuditSinkMode` for MillWorks.** Per D1 (locked 2026-04-25):
   `Immediate` is the platform default; `TransactionalOutbox` is opt-in
   for regulated / zero-loss-durability deployments. MillWorks runs HIPAA
   / FERPA workloads → set `AuditSinkMode = TransactionalOutbox`. Confirm
   only if you want to deviate.

## Verification

```bash
# In MillWorks repo
cd /Users/jesse/RiderProjects/MillWorks
dotnet build MillWorks.sln

# Run MillWorks integration tests (Compliance + Identity + DataProcessing + ...)
dotnet test
```

Acceptance:
- MillWorks builds clean.
- All 54 companion test projects pass.
- `AuditBridge` exposes both `IAuditPublisher` and the existing
  `IFinanceAuditService`; the scoped instance is registered for both
  (one bridge per request scope, matching the rest of the bridge
  taxonomy).
- `DataProcessingDbContext` no longer accepts an interceptor in its
  constructor; no `OnConfiguring` method.
- `Program.cs` attaches the interceptor for DataProcessing centrally,
  matching the Identity/Compliance pattern.
- Manual smoke: create a Compliance record, create a DataProcessing
  upload, create a Finance invoice — `audit.AuditLogs` (or its
  successor) gets a row for each.
- Manual smoke: trigger a fail-closed scenario for a `[PHI]` Compliance
  entity — request fails, both business write AND attempted audit row
  are absent.

## README impact

Phase 10 will:
- Update `MillWorks/README.md:160` (SecurityBridge entry) to remove
  the "audit logging" claim — `SecurityBridge` is no longer the audit
  landing point. Audit is `AuditBridge`.
- Update the same MillWorks README to add `AuditBridge` (currently
  not in the bridge taxonomy table) with its 9-library scope.
- Update `MillWorks.AuditCore/README.md` Quick Start to show the
  central interceptor-registration pattern in Api.

Note in commit/PR; do NOT edit READMEs in this phase.

## Out of scope

- Compliance's inline `AuditLogEntity` mapping removal → Phase 09.
- The 8 libraries that already use Pattern 1 → Phase 09 only updates
  their `using` statements (after Phase 04.5 attribute lift) and
  potentially narrows package references.
- Documentation rewrites → Phase 10.

## Done when

- `AuditBridge` implements `IAuditPublisher` and routes through
  `IAuditSink`.
- DataProcessing no longer self-attaches the interceptor; Api owns the
  attachment centrally.
- MillWorks builds and tests clean.
- Manual smoke tests pass.
- Phase doc updated with "Completed YYYY-MM-DD".
